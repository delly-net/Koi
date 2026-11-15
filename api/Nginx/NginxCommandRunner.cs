using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Koi.Api.Nginx;

/// <summary>一次 nginx 命令的执行结果。</summary>
public sealed record NginxCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTooLarge)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !OutputTooLarge;

    /// <summary>合并 stdout 与 stderr；nginx 的检测结果与版本信息通常写入 stderr。</summary>
    public string CombinedOutput
    {
        get
        {
            var output = StandardError.Trim();
            var standard = StandardOutput.Trim();
            if (output.Length == 0)
            {
                return standard;
            }

            return standard.Length == 0 ? output : $"{output}{Environment.NewLine}{standard}";
        }
    }
}

/// <summary>
/// 通过外部 nginx 命令行执行配置检测与热加载。
/// </summary>
/// <remarks>
/// 统一使用 <see cref="ProcessStartInfo.ArgumentList"/> 传参，绝不拼接 shell 字符串；
/// 参数固定为 <c>-t</c>、<c>-s reload</c>、<c>-v</c> 以及可选的 <c>-c &lt;主配置&gt;</c>，
/// 用户输入只作为路径出现在设置中并经过服务端校验。输出按字节上限截断，超限或超时都会
/// 终止整个进程树，避免管道死锁与内存膨胀。
/// </remarks>
public sealed class NginxCommandRunner(
    NginxSettingsService settingsService,
    ILogger<NginxCommandRunner> logger)
{
    private static readonly string[] NginxCandidatePaths =
    [
        "/usr/sbin/nginx",
        "/usr/local/nginx/sbin/nginx",
        "/usr/bin/nginx",
        "/usr/local/bin/nginx",
        "/sbin/nginx",
    ];

    private NginxOptions Settings => settingsService.Current;

    /// <summary>当前平台能否执行 nginx（Linux 且能找到可执行文件）。</summary>
    public static bool IsAvailable(NginxOptions settings) =>
        OperatingSystem.IsLinux() && ResolveNginx(settings) is not null;

    /// <summary>执行 <c>nginx -t</c> 配置语法检测。</summary>
    public Task<NginxCommandResult> TestAsync(
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken) =>
        RunAsync(BuildArguments("-t", Settings), timeout, maxOutputBytes, cancellationToken);

    /// <summary>执行 <c>nginx -s reload</c> 热加载配置。</summary>
    public Task<NginxCommandResult> ReloadAsync(
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken) =>
        RunAsync(BuildArguments("-s", Settings, "reload"), timeout, maxOutputBytes, cancellationToken);

    /// <summary>执行 <c>nginx -v</c> 读取版本信息。</summary>
    public Task<NginxCommandResult> VersionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunAsync(["-v"], timeout, 64 * 1024, cancellationToken);

    /// <summary>组装固定参数；配置了主配置路径时追加 <c>-c &lt;path&gt;</c>。</summary>
    private static string[] BuildArguments(string option, NginxOptions settings, string? value = null)
    {
        var arguments = new List<string> { option };
        if (value is not null)
        {
            arguments.Add(value);
        }

        if (!string.IsNullOrEmpty(settings.MainConfigPath))
        {
            arguments.Add("-c");
            arguments.Add(settings.MainConfigPath);
        }

        return [.. arguments];
    }

    private async Task<NginxCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new NginxCommandResult(
                -1,
                string.Empty,
                "Nginx 管理依赖 Linux，当前平台不支持。",
                TimedOut: false,
                OutputTooLarge: false);
        }

        var executable = ResolveNginx(Settings);
        if (executable is null)
        {
            return new NginxCommandResult(
                -1,
                string.Empty,
                "未找到 nginx 可执行文件，请确认已安装 nginx，或在 Nginx 管理设置中配置 nginx 路径。",
                TimedOut: false,
                OutputTooLarge: false);
        }

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

        // 固定 locale，保证 nginx 输出与错误文本稳定。
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new NginxCommandResult(
                -1,
                string.Empty,
                $"无法启动 nginx 命令：{ex.Message}",
                TimedOut: false,
                OutputTooLarge: false);
        }

        using (process)
        {
            // nginx 的这些子命令不需要交互输入，立即关闭 stdin。
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout > TimeSpan.Zero)
            {
                timeoutCts.CancelAfter(timeout);
            }

            var truncated = 0;
            void OnTruncated()
            {
                // 某一流超限后立即终止进程，否则子进程会阻塞在写满的管道上。
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
                    "nginx 命令超时：{Arguments}，上限 {Seconds} 秒",
                    string.Join(' ', arguments),
                    timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            }

            return new NginxCommandResult(
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

    /// <summary>定位 nginx 可执行文件：优先配置路径，其次常见安装位置，最后搜索 PATH。</summary>
    private static string? ResolveNginx(NginxOptions settings)
    {
        var configuredPath = settings.NginxPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        foreach (var candidate in NginxCandidatePaths)
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
            var candidate = Path.Combine(directory, "nginx");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
