using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Koi.Api.Packages;

/// <summary>
/// 通过外部包管理器命令行执行查询与变更操作。
/// </summary>
/// <remarks>
/// 统一使用 <see cref="ProcessStartInfo.ArgumentList"/> 传参，绝不拼接 shell 字符串；
/// 子命令由各 <see cref="PackageManagerBackend"/> 固定，用户输入只作为经过校验的包名/源参数。
/// stdout/stderr 并发读取并共享字节上限，超限、超时或取消都会终止整个进程树，
/// 避免子进程阻塞在写满的管道上造成死锁。环境变量固定为非交互模式，
/// 保证 apt/dnf 等不会等待用户输入或打开分页器。
/// </remarks>
public sealed class PackageCommandRunner(ILogger<PackageCommandRunner> logger)
{
    /// <summary>执行一次命令并收集输出（用于查询类接口）。</summary>
    public Task<PackageCommandResult> RunAsync(
        PackageCommand command,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken) =>
        ExecuteAsync(command, timeout, maxOutputBytes, onOutput: null, cancellationToken);

    /// <summary>执行一次命令并把增量输出交给回调（用于后台任务日志）。</summary>
    public Task<PackageCommandResult> RunStreamingAsync(
        PackageCommand command,
        TimeSpan timeout,
        long maxOutputBytes,
        Action<string> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(command, timeout, maxOutputBytes, onOutput, cancellationToken);

    private async Task<PackageCommandResult> ExecuteAsync(
        PackageCommand command,
        TimeSpan timeout,
        long maxOutputBytes,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new PackageCommandResult(
                -1,
                string.Empty,
                "应用管理依赖 Linux 包管理器，当前平台不支持。",
                TimedOut: false,
                OutputTooLarge: false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ConfigureEnvironment(startInfo);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new PackageCommandResult(
                -1,
                string.Empty,
                $"无法启动包管理器命令（{command.FileName}）：{ex.Message}",
                TimedOut: false,
                OutputTooLarge: false);
        }

        using (process)
        {
            // 包管理器命令不需要交互输入，立即关闭 stdin，避免 apt/dnf 等待确认。
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(timeout);
            }

            long totalBytes = 0;
            var truncated = 0;
            void OnTruncated()
            {
                // 超限后立即终止进程：否则子进程会阻塞在写满的管道上，读取任务永远等不到 EOF。
                if (Interlocked.Exchange(ref truncated, 1) == 0)
                {
                    Kill(process);
                }
            }

            long AddBytes(int count) => Interlocked.Add(ref totalBytes, count);

            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput,
                maxOutputBytes,
                onOutput,
                AddBytes,
                OnTruncated,
                timeoutCts.Token);
            var stderrTask = ReadBoundedAsync(
                process.StandardError,
                maxOutputBytes,
                onOutput,
                AddBytes,
                OnTruncated,
                timeoutCts.Token);

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
                // 调用方取消或出现异常时也要回收子进程。
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
                    "包管理器命令超时：{Command} {Arguments}，上限 {Seconds} 秒",
                    command.FileName,
                    string.Join(' ', command.Arguments),
                    timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            }

            return new PackageCommandResult(
                exitCode,
                stdout,
                stderr,
                timedOut,
                Volatile.Read(ref truncated) == 1);
        }
    }

    private static void ConfigureEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["SYSTEMD_COLORS"] = "0";

        // Debian/Ubuntu：禁止 debconf/needrestart 等待交互，并保留本地配置文件。
        startInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        startInfo.Environment["DEBCONF_NONINTERACTIVE_SEEN"] = "true";
        startInfo.Environment["APT_LISTCHANGES_FRONTEND"] = "none";
        startInfo.Environment["NEEDRESTART_MODE"] = "a";
    }

    /// <summary>
    /// 按 UTF-8 字节累计读取一个流；回调不为空时只转发增量文本，否则累积到返回值。
    /// </summary>
    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        long limit,
        Action<string>? onOutput,
        Func<int, long> addBytes,
        Action onTruncated,
        CancellationToken cancellationToken)
    {
        var builder = onOutput is null ? new StringBuilder() : null;
        var buffer = new char[8192];

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                var bytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
                if (limit > 0 && addBytes(bytes) > limit)
                {
                    onTruncated();
                    return builder?.ToString() ?? string.Empty;
                }

                if (onOutput is null)
                {
                    builder!.Append(buffer, 0, read);
                }
                else
                {
                    onOutput(new string(buffer, 0, read));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 超时/取消/进程被终止时返回已读取的内容。
        }

        return builder?.ToString() ?? string.Empty;
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
}
