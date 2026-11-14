using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Koi.Api.Services;

/// <summary>一次 systemctl / journalctl 命令的执行结果。</summary>
public sealed record ServiceCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTooLarge)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !OutputTooLarge;
}

/// <summary>
/// 通过外部 <c>systemctl</c> / <c>journalctl</c> 命令行执行服务管理操作。
/// </summary>
/// <remarks>
/// 统一使用 <see cref="ProcessStartInfo.ArgumentList"/> 传参，绝不拼接 shell 字符串；
/// 子命令白名单由 <see cref="ServiceEndpoints"/> 固定，用户输入只作为经过校验的服务单元名或数值参数。
/// 输出按字节上限截断，超限或超时都会终止整个进程树，避免管道死锁与内存膨胀。
/// </remarks>
public sealed class SystemctlCommandRunner(
    IOptions<ServiceOptions> options,
    ILogger<SystemctlCommandRunner> logger)
{
    private static readonly string[] SystemctlCandidatePaths =
    [
        "/usr/bin/systemctl",
        "/bin/systemctl",
    ];

    private static readonly string[] JournalctlCandidatePaths =
    [
        "/usr/bin/journalctl",
        "/bin/journalctl",
    ];

    private ServiceOptions Settings => options.Value;

    /// <summary>当前平台能否执行 systemctl（Linux 且能找到可执行文件）。</summary>
    public static bool IsAvailable(ServiceOptions settings) =>
        OperatingSystem.IsLinux() && ResolveSystemctl(settings) is not null;

    /// <summary>当前平台能否执行 journalctl（Linux 且能找到可执行文件）。</summary>
    public static bool IsJournalctlAvailable(ServiceOptions settings) =>
        OperatingSystem.IsLinux() && ResolveJournalctl(settings) is not null;

    /// <summary>执行一次 systemctl 命令；调用方负责给出固定的参数数组。</summary>
    public Task<ServiceCommandResult> RunSystemctlAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return NotAvailable("服务管理依赖 Linux 的 systemd，当前平台不支持。");
        }

        var executable = ResolveSystemctl(Settings);
        if (executable is null)
        {
            return NotAvailable(
                "未找到 systemctl 可执行文件，请确认系统使用 systemd 或在 Koi:Services:SystemctlPath 中配置路径。");
        }

        return RunAsync(executable, arguments, timeout, maxOutputBytes, cancellationToken);
    }

    /// <summary>执行一次 journalctl 命令；仅用于读取服务日志快照。</summary>
    public Task<ServiceCommandResult> RunJournalctlAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return NotAvailable("服务日志依赖 Linux 的 journald，当前平台不支持。");
        }

        var executable = ResolveJournalctl(Settings);
        if (executable is null)
        {
            return NotAvailable(
                "未找到 journalctl 可执行文件，请确认系统使用 systemd 或在 Koi:Services:JournalctlPath 中配置路径。");
        }

        return RunAsync(executable, arguments, timeout, maxOutputBytes, cancellationToken);
    }

    private static Task<ServiceCommandResult> NotAvailable(string message) =>
        Task.FromResult(new ServiceCommandResult(
            -1,
            string.Empty,
            message,
            TimedOut: false,
            OutputTooLarge: false));

    private async Task<ServiceCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 固定为 C locale 并关闭颜色，保证列表列宽与状态文本稳定可解析。
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["SYSTEMD_COLORS"] = "0";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new ServiceCommandResult(
                -1,
                string.Empty,
                $"无法启动 {Path.GetFileName(executable)} 命令：{ex.Message}",
                TimedOut: false,
                OutputTooLarge: false);
        }

        using (process)
        {
            // 这些子命令不需要交互输入，立即关闭 stdin，避免子进程等待输入。
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(timeout);
            }

            var truncated = 0;
            void OnTruncated()
            {
                // 某一流超限后立即终止进程：否则子进程会阻塞在写满的管道上，读取任务永远等不到 EOF。
                if (Interlocked.Exchange(ref truncated, 1) == 0)
                {
                    Kill(process);
                }
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxOutputBytes, OnTruncated, timeoutCts.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, maxOutputBytes, OnTruncated, timeoutCts.Token);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                Kill(process);
            }
            finally
            {
                // 调用方取消或出现异常时也要回收子进程，保证不残留 systemctl/journalctl 客户端。
                if (!process.HasExited)
                {
                    Kill(process);
                }
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exitCode = process.HasExited ? process.ExitCode : -1;

            if (timedOut)
            {
                logger.LogWarning(
                    "{Command} 命令超时：{Arguments}，上限 {Seconds} 秒",
                    Path.GetFileName(executable),
                    string.Join(' ', arguments),
                    timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            }

            return new ServiceCommandResult(
                exitCode,
                stdout.Text,
                stderr.Text,
                timedOut,
                Volatile.Read(ref truncated) == 1 || stdout.Truncated || stderr.Truncated);
        }
    }

    /// <summary>按 UTF-8 字节累计读取一个流；超限时触发 <paramref name="onTruncated"/> 并返回已读部分。</summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        long limit,
        Action onTruncated,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        long bytes = 0;

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                bytes += Encoding.UTF8.GetByteCount(buffer, 0, read);
                if (limit > 0 && bytes > limit)
                {
                    onTruncated();
                    return (builder.ToString(), true);
                }

                builder.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 超时/取消/进程被终止时返回已读取的内容。
        }

        return (builder.ToString(), false);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // 进程已退出或无权终止，忽略。
        }
    }

    private static string? ResolveSystemctl(ServiceOptions settings) =>
        ResolveExecutable(settings.SystemctlPath, SystemctlCandidatePaths, "systemctl");

    private static string? ResolveJournalctl(ServiceOptions settings) =>
        ResolveExecutable(settings.JournalctlPath, JournalctlCandidatePaths, "journalctl");

    /// <summary>定位可执行文件：优先配置路径，其次常见安装位置，最后搜索 PATH。</summary>
    private static string? ResolveExecutable(string configuredPath, string[] candidatePaths, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        foreach (var candidate in candidatePaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
