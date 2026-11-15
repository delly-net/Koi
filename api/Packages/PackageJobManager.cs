using System.Text;

namespace Koi.Api.Packages;

/// <summary>一次安装/卸载/升级/刷新索引任务的内存状态。</summary>
/// <remarks>
/// 日志通过增量快照读取；写入时按 UTF-8 字节累计并截断，保证长期输出不会撑爆内存。
/// 任务仅保存在内存中，Koi 重启后丢失。
/// </remarks>
public sealed class PackageJob
{
    private readonly object gate = new();
    private readonly StringBuilder log = new();
    private long logBytes;
    private string status = PackageJobStatuses.Running;
    private int? exitCode;
    private bool truncated;
    private DateTimeOffset? endedAt;

    public PackageJob(string action, IReadOnlyList<string> packages, string userName)
    {
        Action = action;
        Packages = [.. packages];
        UserName = userName;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..16];

    public string Action { get; }

    public string[] Packages { get; }

    public string UserName { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return status == PackageJobStatuses.Running;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (gate)
            {
                return endedAt;
            }
        }
    }

    public void Append(string text, long maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (gate)
        {
            var limit = maxBytes > 0 ? maxBytes : 2 * 1024 * 1024;
            var remaining = limit - logBytes;
            if (remaining <= 0)
            {
                truncated = true;
                return;
            }

            if (text.Length > remaining)
            {
                var take = (int)Math.Min(text.Length, remaining);
                log.Append(text, 0, take);
                logBytes += Encoding.UTF8.GetByteCount(text.AsSpan(0, take));
                truncated = true;
                return;
            }

            log.Append(text);
            logBytes += Encoding.UTF8.GetByteCount(text);
        }
    }

    public void Complete(string finalStatus, int? code)
    {
        lock (gate)
        {
            if (status != PackageJobStatuses.Running)
            {
                return;
            }

            status = finalStatus;
            exitCode = code;
            endedAt = DateTimeOffset.UtcNow;
        }
    }

    public PackageJobSnapshot Snapshot(int offset)
    {
        lock (gate)
        {
            var start = Math.Clamp(offset, 0, log.Length);
            return new PackageJobSnapshot(
                log.ToString(start, log.Length - start),
                log.Length,
                truncated,
                status,
                exitCode,
                endedAt);
        }
    }
}

public sealed record PackageJobSnapshot(
    string Log,
    int NextOffset,
    bool Truncated,
    string Status,
    int? ExitCode,
    DateTimeOffset? EndedAt);

/// <summary>
/// 包管理后台任务管理器：全局同一时间只允许一个任务（避免 apt/dnf 数据库锁冲突），
/// 负责启动、取消、增量日志读取与过期清理。
/// </summary>
public sealed class PackageJobManager : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, PackageJob> jobs = new(StringComparer.Ordinal);
    private readonly PackageCommandRunner runner;
    private readonly PackageSettingsService settings;
    private readonly ILogger<PackageJobManager> logger;
    private readonly IDisposable stoppingRegistration;
    private PackageJob? active;

    public PackageJobManager(
        PackageCommandRunner runner,
        PackageSettingsService settings,
        IHostApplicationLifetime lifetime,
        ILogger<PackageJobManager> logger)
    {
        this.runner = runner;
        this.settings = settings;
        this.logger = logger;

        // 宿主停止时终止正在运行的包管理器，避免服务重启后子进程继续占用包数据库锁。
        stoppingRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            lock (gate)
            {
                active?.Cancellation.Cancel();
            }
        });
    }

    private PackageOptions Settings => settings.Current;

    /// <summary>创建并启动任务；已有任务在运行时返回 <c>null</c>（映射 409）。</summary>
    public PackageJob? TryStart(
        PackageCommand command,
        string action,
        IReadOnlyList<string> packages,
        string userName)
    {
        lock (gate)
        {
            CleanupLocked();
            if (active is { IsRunning: true })
            {
                return null;
            }

            var job = new PackageJob(action, packages, userName);
            jobs[job.Id] = job;
            active = job;

            var timeoutSeconds = Settings.JobTimeoutSeconds > 0 ? Settings.JobTimeoutSeconds : 1800;
            var maxBytes = Settings.MaxLogBytes > 0 ? Settings.MaxLogBytes : 2 * 1024 * 1024;
            _ = Task.Run(() => RunAsync(job, command, TimeSpan.FromSeconds(timeoutSeconds), maxBytes));
            return job;
        }
    }

    public PackageJob? Find(string id)
    {
        lock (gate)
        {
            CleanupLocked();
            return jobs.GetValueOrDefault(id);
        }
    }

    public bool TryCancel(string id)
    {
        var job = Find(id);
        if (job is null || !job.IsRunning)
        {
            return false;
        }

        job.Cancellation.Cancel();
        return true;
    }

    public void Dispose()
    {
        stoppingRegistration.Dispose();
        lock (gate)
        {
            active?.Cancellation.Cancel();
            foreach (var job in jobs.Values)
            {
                job.Cancellation.Dispose();
            }

            jobs.Clear();
        }
    }

    private async Task RunAsync(
        PackageJob job,
        PackageCommand command,
        TimeSpan timeout,
        long maxLogBytes)
    {
        try
        {
            var result = await runner.RunStreamingAsync(
                command,
                timeout,
                maxLogBytes,
                chunk => job.Append(chunk, maxLogBytes),
                job.Cancellation.Token);

            if (job.Cancellation.IsCancellationRequested)
            {
                job.Append("\n[koi] 任务已取消。\n", maxLogBytes);
                job.Complete(PackageJobStatuses.Cancelled, result.ExitCode);
                return;
            }

            if (result.TimedOut)
            {
                job.Append(
                    $"\n[koi] 命令执行超时（上限 {timeout.TotalSeconds:0} 秒），已终止。\n",
                    maxLogBytes);
                job.Complete(PackageJobStatuses.Failed, result.ExitCode);
                return;
            }

            if (result.OutputTooLarge)
            {
                job.Append("\n[koi] 命令输出超过上限，已终止。\n", maxLogBytes);
                job.Complete(PackageJobStatuses.Failed, result.ExitCode);
                return;
            }

            if (command.IsSuccess(result.ExitCode))
            {
                job.Complete(PackageJobStatuses.Succeeded, result.ExitCode);
                return;
            }

            job.Append($"\n[koi] 命令退出码 {result.ExitCode}。\n", maxLogBytes);
            job.Complete(PackageJobStatuses.Failed, result.ExitCode);
        }
        catch (OperationCanceledException)
        {
            job.Append("\n[koi] 任务已取消。\n", maxLogBytes);
            job.Complete(PackageJobStatuses.Cancelled, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "包管理任务 {JobId} 执行失败", job.Id);
            job.Append($"\n[koi] 执行失败：{ex.Message}\n", maxLogBytes);
            job.Complete(PackageJobStatuses.Failed, null);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, job))
                {
                    active = null;
                }
            }
        }
    }

    private void CleanupLocked()
    {
        var retentionMinutes = Settings.JobRetentionMinutes > 0 ? Settings.JobRetentionMinutes : 30;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-retentionMinutes);

        foreach (var (id, job) in jobs.ToArray())
        {
            if (job.Status == PackageJobStatuses.Running)
            {
                continue;
            }

            if (job.EndedAt is { } endedAt && endedAt < cutoff)
            {
                jobs.Remove(id);
                job.Cancellation.Dispose();
            }
        }

        if (active is not null && !active.IsRunning)
        {
            active = null;
        }
    }
}
