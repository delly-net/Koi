using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Koi.Api.Docker;

/// <summary>一次 docker 命令的执行结果。</summary>
public sealed record DockerCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTooLarge)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !OutputTooLarge;
}

/// <summary>
/// 通过外部 <c>docker</c> 命令行执行容器管理操作。
/// </summary>
/// <remarks>
/// 统一使用 <see cref="ProcessStartInfo.ArgumentList"/> 传参，绝不拼接 shell 字符串；
/// 子命令白名单由 <see cref="DockerEndpoints"/> 固定，用户输入只作为经过校验的标识/引用参数。
/// 输出按字节上限截断，超限或超时都会终止整个进程树，避免管道死锁与内存膨胀。
/// </remarks>
public sealed class DockerCommandRunner(IOptions<DockerOptions> options, ILogger<DockerCommandRunner> logger)
{
    private static readonly string[] CandidatePaths =
    [
        "/usr/bin/docker",
        "/usr/local/bin/docker",
        "/bin/docker",
    ];

    private DockerOptions Settings => options.Value;

    /// <summary>当前平台能否执行 docker 命令（Linux 且能找到可执行文件）。</summary>
    public static bool IsAvailable(DockerOptions settings) =>
        OperatingSystem.IsLinux() && ResolveExecutable(settings) is not null;

    /// <summary>执行一次 docker 命令；调用方负责给出固定的参数数组。</summary>
    public async Task<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new DockerCommandResult(
                -1,
                string.Empty,
                "容器管理依赖 Linux 上的 Docker，当前平台不支持。",
                TimedOut: false,
                OutputTooLarge: false);
        }

        var executable = ResolveExecutable(Settings);
        if (executable is null)
        {
            return new DockerCommandResult(
                -1,
                string.Empty,
                "未找到 docker 可执行文件，请安装 Docker 或在 Koi:Docker:DockerPath 中配置路径。",
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

        // 固定为 C locale，保证 --format '{{json .}}' 的字段与数值格式稳定。
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["DOCKER_CLI_HINTS"] = "false";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new DockerCommandResult(
                -1,
                string.Empty,
                $"无法启动 docker 命令：{ex.Message}",
                TimedOut: false,
                OutputTooLarge: false);
        }

        using (process)
        {
            // docker 的这些子命令不需要交互输入，立即关闭 stdin，避免子进程等待输入。
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
                // 调用方取消或出现异常时也要回收子进程，保证不残留 docker 客户端。
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
                    "docker 命令超时：{Arguments}，上限 {Seconds} 秒",
                    string.Join(' ', arguments),
                    timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            }

            return new DockerCommandResult(
                exitCode,
                stdout.Text,
                stderr.Text,
                timedOut,
                Volatile.Read(ref truncated) == 1 || stdout.Truncated || stderr.Truncated);
        }
    }

    /// <summary>执行 <c>--format '{{json .}}'</c> 类命令，把逐行 JSON 解析为元素数组。</summary>
    public async Task<(DockerCommandResult Result, IReadOnlyList<JsonElement> Items)> RunJsonLinesAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, timeout, maxOutputBytes, cancellationToken);
        if (!result.Ok)
        {
            return (result, Array.Empty<JsonElement>());
        }

        var items = new List<JsonElement>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                items.Add(document.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "无法解析 docker 命令输出行：{Line}", text);
            }
        }

        return (result, items);
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

    /// <summary>定位 docker 可执行文件：优先配置路径，其次常见安装位置，最后搜索 PATH。</summary>
    private static string? ResolveExecutable(DockerOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DockerPath))
        {
            return File.Exists(settings.DockerPath) ? settings.DockerPath : null;
        }

        foreach (var candidate in CandidatePaths)
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
            var candidate = Path.Combine(directory, "docker");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
