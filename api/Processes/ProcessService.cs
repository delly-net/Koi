using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Koi.Api.Processes;

/// <summary>
/// Linux 进程采集与结束：直接读取 <c>/proc</c>，不调用 <c>ps</c> 等外部命令。
/// </summary>
/// <remarks>
/// CPU 占比来自 <c>/proc/[pid]/stat</c> 的 <c>utime + stime</c>（jiffies）。单例保存上一次原始采样：
/// 距上次采样不超过 30 秒时直接复用为基线，得到两次轮询之间的真实占用；首次请求或长时间未采样时
/// 在内部做一次 <see cref="ProcessOptions.SampleWindowMilliseconds"/> 的短采样。首次出现的进程没有
/// 基线，改用生命周期平均值，避免首屏全部为 0。快照缓存 1 秒，防止多个客户端并发扫描 <c>/proc</c>。
/// <para>
/// 结束进程通过 <c>kill(2)</c> 发送 SIGTERM / SIGKILL，是主进程唯一的特权动作；PID 1 与 Koi 自身
/// 进程永远拒绝。授权由 <see cref="ProcessAccessGuard"/> 在端点层完成。
/// </para>
/// </remarks>
public sealed class ProcessService
{
    /// <summary>超过该秒数未采样时视为过期，重新做短采样。</summary>
    private const double MaxSampleAgeSeconds = 30;

    /// <summary>快照缓存秒数。</summary>
    private const double CachedSnapshotSeconds = 1;

    /// <summary>原始命令行采集上限（详情展示用），避免巨型 argv 长时间占用内存。</summary>
    private const int MaxRawCommandLineLength = 4096;

    private const int SignalTerminate = 15;
    private const int SignalKill = 9;
    private const int ErrnoPermission = 1;
    private const int ErrnoNoProcess = 3;
    private const int ScClockTicksPerSecond = 2;

    private static readonly long PageSize = Environment.SystemPageSize;
    private static readonly int ClockTicksPerSecond = ReadClockTicksPerSecond();

    private readonly ProcessSettingsService settingsService;
    private readonly ILogger<ProcessService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<uint, string> userNames = new();

    private RawSnapshot? previous;
    private DateTimeOffset previousAt;
    private ProcessListResponse? cached;
    private DateTimeOffset cachedAt;

    public ProcessService(ProcessSettingsService settingsService, ILogger<ProcessService> logger)
    {
        this.settingsService = settingsService;
        this.logger = logger;
    }

    /// <summary>获取一次进程列表；并发调用串行化，1 秒内复用缓存。</summary>
    public async Task<ProcessListResponse> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (cached is { } cachedResponse && (now - cachedAt).TotalSeconds < CachedSnapshotSeconds)
            {
                return cachedResponse;
            }

            // 每次读取最新快照，保证设置保存后立即生效。
            var settings = settingsService.Current;
            var window = TimeSpan.FromMilliseconds(Math.Clamp(settings.SampleWindowMilliseconds, 50, 5000));

            RawSnapshot baseline;
            DateTimeOffset baselineAt;
            if (previous is { } previousSnapshot && (now - previousAt).TotalSeconds <= MaxSampleAgeSeconds)
            {
                baseline = previousSnapshot;
                baselineAt = previousAt;
            }
            else
            {
                baseline = Capture();
                baselineAt = DateTimeOffset.UtcNow;
                await Task.Delay(window, cancellationToken);
            }

            var current = Capture();
            var currentAt = DateTimeOffset.UtcNow;
            var elapsedSeconds = Math.Max((currentAt - baselineAt).TotalSeconds, 0.001);

            previous = current;
            previousAt = currentAt;

            var cpuCeiling = 100.0 * Environment.ProcessorCount;
            var summaries = new List<ProcessSummary>(current.Processes.Count);
            foreach (var sample in current.Processes.Values)
            {
                double cpuPercent;
                if (baseline.Processes.TryGetValue(sample.Pid, out var previousSample) &&
                    sample.Ticks >= previousSample.Ticks)
                {
                    var deltaTicks = sample.Ticks - previousSample.Ticks;
                    cpuPercent = deltaTicks / (double)ClockTicksPerSecond / elapsedSeconds * 100;
                }
                else
                {
                    // 新进程没有采样基线：用生命周期平均值（与 ps 的 %CPU 语义一致）。
                    var lifetimeSeconds = Math.Max(current.UptimeSeconds - sample.StartSeconds, 0.01);
                    cpuPercent = sample.Ticks / (double)ClockTicksPerSecond / lifetimeSeconds * 100;
                }

                cpuPercent = Math.Round(Math.Clamp(cpuPercent, 0, cpuCeiling), 1);
                var runtimeSeconds = Math.Max(current.UptimeSeconds - sample.StartSeconds, 0);
                var memoryPercent = current.MemTotalBytes > 0
                    ? Math.Round(Math.Clamp(sample.RssBytes * 100.0 / current.MemTotalBytes, 0, 100), 1)
                    : 0;

                summaries.Add(new ProcessSummary(
                    Pid: sample.Pid,
                    ParentPid: sample.ParentPid,
                    Name: sample.Name,
                    User: ResolveUserName(sample.Uid),
                    Uid: sample.Uid,
                    State: sample.State,
                    CpuPercent: cpuPercent,
                    MemoryPercent: memoryPercent,
                    RssBytes: sample.RssBytes,
                    VirtualBytes: sample.VirtualBytes,
                    Threads: sample.Threads,
                    RuntimeSeconds: Math.Round(runtimeSeconds, 1),
                    CommandLine: Truncate(sample.CommandLine, settings.MaxCommandLineLength)));
            }

            var total = summaries.Count;
            var processes = summaries
                .OrderByDescending(process => process.CpuPercent)
                .ThenByDescending(process => process.RssBytes)
                .Take(settings.MaxProcesses)
                .ToArray();

            var response = new ProcessListResponse(
                processes,
                total,
                total > settings.MaxProcesses,
                currentAt,
                settings.RefreshIntervalSeconds);

            cached = response;
            cachedAt = currentAt;
            return response;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>读取单个进程详情；进程已不存在时返回 <c>null</c>。</summary>
    public async Task<ProcessDetail?> GetDetailAsync(int pid, CancellationToken cancellationToken)
    {
        var list = await GetSnapshotAsync(cancellationToken);
        var settings = settingsService.Current;
        var summary = list.Processes.FirstOrDefault(process => process.Pid == pid);

        if (summary is null)
        {
            // 列表按 CPU/条数截断时，仍允许按 PID 直接读取详情。
            var raw = ReadRawProcess(pid);
            if (raw is null)
            {
                return null;
            }

            var uptimeSeconds = ReadUptimeSeconds();
            var lifetimeSeconds = Math.Max(uptimeSeconds - raw.StartSeconds, 0.01);
            var cpuPercent = Math.Round(
                Math.Clamp(raw.Ticks / (double)ClockTicksPerSecond / lifetimeSeconds * 100, 0, 100.0 * Environment.ProcessorCount),
                1);
            var memTotalBytes = ReadMemTotalBytes();
            var memoryPercent = memTotalBytes > 0
                ? Math.Round(Math.Clamp(raw.RssBytes * 100.0 / memTotalBytes, 0, 100), 1)
                : 0;

            summary = new ProcessSummary(
                raw.Pid,
                raw.ParentPid,
                raw.Name,
                ResolveUserName(raw.Uid),
                raw.Uid,
                raw.State,
                cpuPercent,
                memoryPercent,
                raw.RssBytes,
                raw.VirtualBytes,
                raw.Threads,
                Math.Round(Math.Max(uptimeSeconds - raw.StartSeconds, 0), 1),
                Truncate(raw.CommandLine, settings.MaxCommandLineLength));
        }

        var (readBytes, writeBytes) = ReadIo(pid);
        return new ProcessDetail(
            Pid: summary.Pid,
            ParentPid: summary.ParentPid,
            Name: summary.Name,
            User: summary.User,
            Uid: summary.Uid,
            State: summary.State,
            CpuPercent: summary.CpuPercent,
            MemoryPercent: summary.MemoryPercent,
            RssBytes: summary.RssBytes,
            VirtualBytes: summary.VirtualBytes,
            Threads: summary.Threads,
            RuntimeSeconds: summary.RuntimeSeconds,
            CommandLine: ReadCommandLine(pid, summary.Name, MaxRawCommandLineLength),
            ExecutablePath: ReadLinkTarget($"/proc/{pid}/exe"),
            WorkingDirectory: ReadLinkTarget($"/proc/{pid}/cwd"),
            ReadBytes: readBytes,
            WriteBytes: writeBytes,
            OpenFileCount: CountOpenFiles(pid),
            SampledAt: list.SampledAt);
    }

    /// <summary>结束进程：SIGTERM（force=false）或 SIGKILL（force=true），并等待其退出。</summary>
    public async Task<ProcessTerminateOutcome> TerminateAsync(
        int pid,
        bool force,
        CancellationToken cancellationToken)
    {
        // PID 1 与 Koi 自身进程永不允许结束：前者是 init/systemd，后者会让当前页面立即断开。
        if (pid <= 1 || pid == Environment.ProcessId)
        {
            return new ProcessTerminateOutcome(ProcessTerminateStatus.Protected, 0);
        }

        if (!Directory.Exists($"/proc/{pid}"))
        {
            return new ProcessTerminateOutcome(ProcessTerminateStatus.NotFound, 0);
        }

        var signal = force ? SignalKill : SignalTerminate;
        if (KillNative(pid, signal) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == ErrnoNoProcess)
            {
                return new ProcessTerminateOutcome(ProcessTerminateStatus.NotFound, 0);
            }

            if (errno == ErrnoPermission)
            {
                return new ProcessTerminateOutcome(ProcessTerminateStatus.PermissionDenied, 0);
            }
        }

        var settings = settingsService.Current;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TerminateTimeoutSeconds, 1, 60));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            // 已退出或变成僵尸（等待父进程回收）都视为结束成功。
            if (IsTerminated(pid))
            {
                return new ProcessTerminateOutcome(ProcessTerminateStatus.Exited, stopwatch.ElapsedMilliseconds);
            }

            await Task.Delay(100, cancellationToken);
        }

        return new ProcessTerminateOutcome(ProcessTerminateStatus.StillRunning, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>扫描 /proc 得到一次原始采样。</summary>
    private RawSnapshot Capture()
    {
        var uptimeSeconds = ReadUptimeSeconds();
        var memTotalBytes = ReadMemTotalBytes();
        var processes = new Dictionary<int, RawProcess>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    continue;
                }

                var sample = ReadRawProcess(pid);
                if (sample is not null)
                {
                    processes[pid] = sample;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // /proc 不可读由端点映射为 503；这里保留已采集到的部分。
            logger.LogDebug(exception, "扫描 /proc 时提前结束。");
        }

        return new RawSnapshot(processes, uptimeSeconds, memTotalBytes);
    }

    /// <summary>读取单个进程的 stat / status / cmdline；进程已退出或不可读时返回 <c>null</c>。</summary>
    private RawProcess? ReadRawProcess(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var open = stat.IndexOf('(');
            var close = stat.LastIndexOf(')');
            if (open <= 0 || close <= open)
            {
                return null;
            }

            var pidValue = int.TryParse(stat[..open].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid)
                ? parsedPid
                : pid;
            var name = stat[(open + 1)..close];
            var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 22)
            {
                return null;
            }

            var state = fields[0];
            var parentPid = (int)ParseLong(fields[1]);

            // 字段（1 起）：3 state、4 ppid、14 utime、15 stime、20 num_threads、22 starttime、
            // 23 vsize（字节）、24 rss（页）。
            var ticks = ParseLong(fields[11]) + ParseLong(fields[12]);
            var threads = (int)ParseLong(fields[17]);
            // starttime 是自内核启动以来的 jiffies，换算为「自启动以来的秒数」，
            // 与 /proc/uptime 同一时间基准（不能再叠加 btime，否则运行时长会被夹到 0）。
            var startSeconds = ParseLong(fields[19]) / (double)ClockTicksPerSecond;
            var virtualBytes = ParseLong(fields[20]);
            var rssBytes = ParseLong(fields[21]) * PageSize;

            var uid = ReadUid($"/proc/{pid}/status");
            var commandLine = ReadCommandLine(pid, name, MaxRawCommandLineLength);

            return new RawProcess(
                pidValue,
                parentPid,
                name,
                state,
                ticks,
                rssBytes,
                virtualBytes,
                threads,
                startSeconds,
                uid,
                commandLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 进程在枚举与读取之间退出属于正常竞态，跳过即可。
            logger.LogDebug(exception, "跳过进程 {Pid}。", pid);
            return null;
        }
    }

    /// <summary>读取 /proc/[pid]/status 的 Uid 行首个值（真实 uid）。</summary>
    private static uint ReadUid(string statusPath)
    {
        foreach (var line in File.ReadLines(statusPath))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 &&
                uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            {
                return uid;
            }
        }

        return 0;
    }

    /// <summary>读取 /proc/[pid]/cmdline（NUL 分隔）；内核线程为空时回退为 [comm]。</summary>
    private static string ReadCommandLine(int pid, string name, int maxLength)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
            if (bytes.Length == 0)
            {
                return $"[{name}]";
            }

            var text = Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
            return Truncate(text.Length == 0 ? $"[{name}]" : text, maxLength);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"[{name}]";
        }
    }

    /// <summary>读取可执行文件或工作目录符号链接；失败时返回空串。</summary>
    private static string ReadLinkTarget(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? new FileInfo(path).LinkTarget ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>读取 /proc/[pid]/io 的累计读写字节。</summary>
    private static (long? ReadBytes, long? WriteBytes) ReadIo(int pid)
    {
        long? read = null;
        long? write = null;
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/io"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 ||
                    !long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                switch (line[..colon])
                {
                    case "read_bytes":
                        read = value;
                        break;
                    case "write_bytes":
                        write = value;
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        return (read, write);
    }

    /// <summary>统计进程打开的文件描述符数量。</summary>
    private static int? CountOpenFiles(int pid)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd").Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>进程已退出或处于僵尸/死亡状态时返回 <c>true</c>。</summary>
    private static bool IsTerminated(int pid)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length)
        {
            return true;
        }

        // 形如 "pid (comm) S ..."，右括号后隔一个空格是状态字母。
        var state = text[close + 2];
        return state is 'Z' or 'X' or 'x';
    }

    /// <summary>uid → 用户名（getpwuid_r，尊重 NSS/LDAP），缓存到进程内字典。</summary>
    private string ResolveUserName(uint uid) => userNames.GetOrAdd(uid, LookupUserName);

    private static string LookupUserName(uint uid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return uid.ToString(CultureInfo.InvariantCulture);
        }

        var buffer = Marshal.AllocHGlobal(8192);
        var passwd = Marshal.AllocHGlobal(64);
        try
        {
            var status = getpwuid_r(uid, passwd, buffer, (nuint)8192, out var result);
            if (status == 0 && result != IntPtr.Zero)
            {
                var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result, 0));
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 glibc 平台回退为数字 uid。
        }
        finally
        {
            Marshal.FreeHGlobal(passwd);
            Marshal.FreeHGlobal(buffer);
        }

        return uid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>读取 /proc/uptime 第一列（秒）。</summary>
    private static double ReadUptimeSeconds()
    {
        var parts = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 &&
               double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(value, 0)
            : 0;
    }

    /// <summary>读取 /proc/meminfo 的 MemTotal（字节）。</summary>
    private static long ReadMemTotalBytes()
    {
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line[9..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 &&
                long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
            {
                return kilobytes * 1024;
            }
        }

        return 0;
    }

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    /// <summary>按字符数截断并追加省略号。</summary>
    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}…";
    }

    /// <summary>读取 jiffies 每秒计数（sysconf(_SC_CLK_TCK)）；非 Linux 回退 100。</summary>
    private static int ReadClockTicksPerSecond()
    {
        try
        {
            var value = sysconf(ScClockTicksPerSecond);
            return value > 0 ? (int)value : 100;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return 100;
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillNative(int pid, int signal);

    [DllImport("libc", EntryPoint = "getpwuid_r", SetLastError = true)]
    private static extern int getpwuid_r(uint uid, IntPtr passwd, IntPtr buffer, nuint bufferLength, out IntPtr result);

    [DllImport("libc", EntryPoint = "sysconf")]
    private static extern long sysconf(int name);

    private sealed record RawProcess(
        int Pid,
        int ParentPid,
        string Name,
        string State,
        long Ticks,
        long RssBytes,
        long VirtualBytes,
        int Threads,
        double StartSeconds,
        uint Uid,
        string CommandLine);

    private sealed record RawSnapshot(
        Dictionary<int, RawProcess> Processes,
        double UptimeSeconds,
        long MemTotalBytes);
}
