using System.Globalization;

namespace Koi.Api.Monitor;

/// <summary>
/// Linux 服务器状态采集：CPU、内存与交换分区、磁盘、网络速率与主机信息。
/// </summary>
/// <remarks>
/// CPU 与网络来自 <c>/proc</c> 的累计计数器，必须用两次采样的差值计算。单例保存上一次计数器，
/// 正常轮询（5 秒）直接复用；首次请求或上次采样超过 30 秒时在内部做一次 250ms 短采样，
/// 避免把长时间空闲平均成“当前速率”。结果缓存 1 秒，防止多个客户端同时请求时以极小间隔
/// 采样导致数值抖动。磁盘通过 <see cref="DriveInfo"/>（statvfs）读取，伪文件系统被过滤。
/// </remarks>
public sealed class SystemMonitorService(ILogger<SystemMonitorService> logger)
{
    /// <summary>内部短采样窗口（毫秒）。</summary>
    private const int SampleWindowMilliseconds = 250;

    /// <summary>超过该秒数未采样时视为过期，重新做短采样。</summary>
    private const double MaxCounterAgeSeconds = 30;

    /// <summary>快照缓存秒数。</summary>
    private const double CachedSnapshotSeconds = 1;

    /// <summary>磁盘列表最多返回的挂载点数量。</summary>
    private const int MaxDisks = 20;

    /// <summary>伪文件系统黑名单：其容量不代表真实磁盘，不展示。</summary>
    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "devfs", "proc", "sysfs", "cgroup", "cgroup2", "overlay", "squashfs",
        "ramfs", "rootfs", "devpts", "mqueue", "securityfs", "debugfs", "tracefs", "bpf",
        "configfs", "fusectl", "hugetlbfs", "pstore", "autofs", "nsfs", "efivarfs", "selinuxfs",
        "rpc_pipefs", "binfmt_misc",
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    private CpuTimes? previousCpu;
    private NetworkTotals? previousNetwork;
    private DateTimeOffset previousSampleAt;
    private SystemStatusSnapshot? cachedSnapshot;
    private DateTimeOffset cachedAt;

    /// <summary>获取一次服务器状态快照；并发调用串行化，1 秒内复用缓存。</summary>
    public async Task<SystemStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (cachedSnapshot is not null && (now - cachedAt).TotalSeconds < CachedSnapshotSeconds)
            {
                return cachedSnapshot;
            }

            var baselineAt = DateTimeOffset.UtcNow;
            var baselineCpu = ReadCpuTimes();
            var baselineNetwork = ReadNetworkTotals();

            if (previousCpu is { } cpuBaseline && previousNetwork is { } networkBaseline &&
                (baselineAt - previousSampleAt).TotalSeconds <= MaxCounterAgeSeconds)
            {
                // 正常轮询：以上一次请求的采样作为基线，得到真实的 5 秒窗口速率。
                baselineAt = previousSampleAt;
                baselineCpu = cpuBaseline;
                baselineNetwork = networkBaseline;
            }
            else
            {
                // 首次请求或长时间未采样：内部短采样，避免返回 0 或历史平均值。
                await Task.Delay(SampleWindowMilliseconds, cancellationToken);
            }

            var currentAt = DateTimeOffset.UtcNow;
            var currentCpu = ReadCpuTimes();
            var currentNetwork = ReadNetworkTotals();
            var elapsedSeconds = Math.Max((currentAt - baselineAt).TotalSeconds, 0.001);

            previousCpu = currentCpu;
            previousNetwork = currentNetwork;
            previousSampleAt = currentAt;

            var memory = ReadMemory();
            var disks = ReadDisks();
            var load = ReadLoadAverage();

            var snapshot = new SystemStatusSnapshot(
                HostName: Environment.MachineName,
                KernelVersion: ReadKernelVersion(),
                UptimeSeconds: ReadUptimeSeconds(),
                ProcessCount: ReadProcessCount(),
                Cpu: new CpuStatus(
                    UsagePercent: CalculateCpuUsage(baselineCpu, currentCpu),
                    Cores: Environment.ProcessorCount,
                    Load1: load.Load1,
                    Load5: load.Load5,
                    Load15: load.Load15),
                Memory: memory,
                Disks: disks,
                Network: new NetworkStatus(
                    ReceivedBytesPerSecond: CalculateRate(baselineNetwork.ReceivedBytes, currentNetwork.ReceivedBytes, elapsedSeconds),
                    SentBytesPerSecond: CalculateRate(baselineNetwork.SentBytes, currentNetwork.SentBytes, elapsedSeconds),
                    ReceivedTotalBytes: currentNetwork.ReceivedBytes,
                    SentTotalBytes: currentNetwork.SentBytes),
                SampledAt: currentAt);

            cachedSnapshot = snapshot;
            cachedAt = currentAt;
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>读取 /proc/stat 第一行的累计 CPU 时间（total 与 idle+iowait）。</summary>
    private static CpuTimes ReadCpuTimes()
    {
        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long total = 0;
            long idle = 0;
            // 字段：user nice system idle iowait irq softirq steal（guest 已计入 user）。
            for (var index = 1; index < parts.Length && index <= 8; index++)
            {
                if (!long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    break;
                }

                total += value;
                if (index is 4 or 5)
                {
                    idle += value;
                }
            }

            return new CpuTimes(total, idle);
        }

        throw new IOException("/proc/stat 缺少 cpu 统计行。");
    }

    /// <summary>CPU 使用率：两次采样中非空闲时间占比。</summary>
    private static double CalculateCpuUsage(CpuTimes baseline, CpuTimes current)
    {
        var totalDelta = current.Total - baseline.Total;
        var idleDelta = current.Idle - baseline.Idle;
        if (totalDelta <= 0 || idleDelta < 0)
        {
            return 0;
        }

        return ClampPercent((1 - (double)idleDelta / totalDelta) * 100);
    }

    /// <summary>读取 /proc/meminfo 的内存与交换分区使用量。</summary>
    private static MemoryStatus ReadMemory()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var parts = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            // meminfo 的容量字段单位为 kB；HugePages 等计数类字段没有单位，保持原值。
            var bytes = parts.Length > 1 && parts[1].Equals("kB", StringComparison.OrdinalIgnoreCase)
                ? value * 1024
                : value;
            values[line[..colon]] = bytes;
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            throw new IOException("/proc/meminfo 缺少 MemTotal。");
        }

        // 现代内核提供 MemAvailable；缺失时用 MemFree + Buffers + Cached 兜底。
        if (!values.TryGetValue("MemAvailable", out var available))
        {
            available = values.GetValueOrDefault("MemFree")
                + values.GetValueOrDefault("Buffers")
                + values.GetValueOrDefault("Cached");
        }

        available = Math.Clamp(available, 0, total);
        var used = total - available;

        var swapTotal = values.GetValueOrDefault("SwapTotal");
        var swapFree = Math.Clamp(values.GetValueOrDefault("SwapFree"), 0, Math.Max(swapTotal, 0));
        var swapUsed = Math.Max(swapTotal - swapFree, 0);

        return new MemoryStatus(
            TotalBytes: total,
            UsedBytes: used,
            AvailableBytes: available,
            UsagePercent: Percent(used, total),
            SwapTotalBytes: Math.Max(swapTotal, 0),
            SwapUsedBytes: swapUsed,
            SwapUsagePercent: Percent(swapUsed, swapTotal));
    }

    /// <summary>读取真实磁盘挂载点（过滤伪文件系统，逐项容错，最多 20 条）。</summary>
    private IReadOnlyList<DiskStatus> ReadDisks()
    {
        var disks = new List<DiskStatus>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || PseudoFileSystems.Contains(drive.DriveFormat))
                {
                    continue;
                }

                var total = drive.TotalSize;
                if (total <= 0)
                {
                    continue;
                }

                var free = Math.Clamp(drive.AvailableFreeSpace, 0, total);
                var used = total - free;
                disks.Add(new DiskStatus(
                    MountPoint: drive.Name,
                    FileSystem: drive.DriveFormat,
                    TotalBytes: total,
                    UsedBytes: used,
                    FreeBytes: free,
                    UsagePercent: Percent(used, total)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 无响应的 NFS/FUSE 或权限不足的挂载点不影响其余磁盘与整个接口。
                logger.LogDebug(exception, "跳过无法读取的挂载点 {MountPoint}。", drive.Name);
            }
        }

        return disks
            .OrderBy(disk => disk.MountPoint == "/" ? string.Empty : disk.MountPoint, StringComparer.Ordinal)
            .Take(MaxDisks)
            .ToArray();
    }

    /// <summary>读取 /proc/net/dev 除 lo 外全部接口的收发累计字节。</summary>
    private static NetworkTotals ReadNetworkTotals()
    {
        long received = 0;
        long sent = 0;
        foreach (var line in File.ReadLines("/proc/net/dev"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (line[..colon].Trim().Equals("lo", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9)
            {
                continue;
            }

            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx))
            {
                received += rx;
            }

            if (long.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx))
            {
                sent += tx;
            }
        }

        return new NetworkTotals(received, sent);
    }

    /// <summary>由累计字节差换算每秒速率；计数器回绕（负差）按 0 处理。</summary>
    private static double CalculateRate(long baseline, long current, double elapsedSeconds)
    {
        var delta = current - baseline;
        if (delta <= 0)
        {
            return 0;
        }

        var rate = delta / elapsedSeconds;
        return double.IsFinite(rate) ? Math.Round(rate, 1) : 0;
    }

    /// <summary>读取 /proc/loadavg 的 1/5/15 分钟负载均值。</summary>
    private static (double Load1, double Load5, double Load15) ReadLoadAverage()
    {
        var parts = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 3
            ? (0, 0, 0)
            : (ParseDouble(parts[0]), ParseDouble(parts[1]), ParseDouble(parts[2]));
    }

    /// <summary>读取 /proc/uptime 第一列（秒）。</summary>
    private static double ReadUptimeSeconds()
    {
        var parts = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? Math.Max(ParseDouble(parts[0]), 0) : 0;
    }

    /// <summary>读取内核版本。</summary>
    private static string ReadKernelVersion()
    {
        try
        {
            var release = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
            return release.Length > 0 ? release : Environment.OSVersion.VersionString;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Environment.OSVersion.VersionString;
        }
    }

    /// <summary>统计 /proc 下的数字目录数量作为进程数。</summary>
    private static int ReadProcessCount()
    {
        try
        {
            return Directory.EnumerateDirectories("/proc")
                .Count(path => int.TryParse(Path.GetFileName(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static double ParseDouble(string value) => double.TryParse(
        value,
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var parsed) && double.IsFinite(parsed)
        ? parsed
        : 0;

    /// <summary>百分比：保留 1 位小数并夹紧到 0..100；总量为 0 时返回 0。</summary>
    private static double Percent(long used, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return ClampPercent((double)used * 100 / total);
    }

    private static double ClampPercent(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, 0, 100), 1) : 0;

    private readonly record struct CpuTimes(long Total, long Idle);

    private readonly record struct NetworkTotals(long ReceivedBytes, long SentBytes);
}

/// <summary>一次服务器状态快照（字段与 <c>GET /api/system/status</c> 的 JSON 一致）。</summary>
public sealed record SystemStatusSnapshot(
    string HostName,
    string KernelVersion,
    double UptimeSeconds,
    int ProcessCount,
    CpuStatus Cpu,
    MemoryStatus Memory,
    IReadOnlyList<DiskStatus> Disks,
    NetworkStatus Network,
    DateTimeOffset SampledAt);

/// <summary>CPU 使用率、核心数与负载均值。</summary>
public sealed record CpuStatus(double UsagePercent, int Cores, double Load1, double Load5, double Load15);

/// <summary>内存与交换分区使用量（字节）。</summary>
public sealed record MemoryStatus(
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    double UsagePercent,
    long SwapTotalBytes,
    long SwapUsedBytes,
    double SwapUsagePercent);

/// <summary>单个磁盘挂载点的容量与使用率。</summary>
public sealed record DiskStatus(
    string MountPoint,
    string FileSystem,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent);

/// <summary>网络累计收发字节与每秒速率。</summary>
public sealed record NetworkStatus(
    double ReceivedBytesPerSecond,
    double SentBytesPerSecond,
    long ReceivedTotalBytes,
    long SentTotalBytes);
