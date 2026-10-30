using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Koi.Api.Files;

/// <summary>
/// 主进程侧的文件操作入口：以隐藏参数拉起文件辅助进程，写入请求并读取响应。
/// 文件读写全部发生在降权后的辅助进程内，主进程（可能以 root 运行）不直接触碰文件系统。
/// </summary>
public sealed class FileOperationService(IOptions<FileOptions> options, ILogger<FileOperationService> logger)
{
    private const int CopyBufferSize = 81920;

    /// <summary>执行普通操作（列表、新建、重命名、删除、复制、移动）。</summary>
    public Task<FileHelperResponse> ExecuteAsync(FileHelperRequest request, CancellationToken cancellationToken) =>
        RunAsync(request, uploadContent: null, downloadOutput: null, onDownloadHeader: null, cancellationToken);

    /// <summary>上传：请求头之后把请求体流式写入辅助进程的标准输入。</summary>
    public Task<FileHelperResponse> UploadAsync(
        FileHelperRequest request,
        Stream content,
        CancellationToken cancellationToken) =>
        RunAsync(request, content, downloadOutput: null, onDownloadHeader: null, cancellationToken);

    /// <summary>
    /// 下载：先读取辅助进程响应头，确认成功后由 <paramref name="onDownloadHeader"/> 设置响应头，
    /// 再把标准输出剩余字节流式写入 <paramref name="output"/>。
    /// </summary>
    public Task<FileHelperResponse> DownloadAsync(
        FileHelperRequest request,
        Func<FileHelperResponse, Task> onDownloadHeader,
        Stream output,
        CancellationToken cancellationToken) =>
        RunAsync(request, uploadContent: null, output, onDownloadHeader, cancellationToken);

    private async Task<FileHelperResponse> RunAsync(
        FileHelperRequest request,
        Stream? uploadContent,
        Stream? downloadOutput,
        Func<FileHelperResponse, Task>? onDownloadHeader,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return FileHelperResponse.Failure(
                FileErrorCodes.HelperFailed,
                "文件操作依赖 Linux 文件系统权限，当前平台不支持。");
        }

        // 超时按操作类型分级：目录列举、递归传输与流式上传/下载各有独立上限，
        // 流式操作按「有数据流动即续期」计算空闲超时，避免慢客户端被固定墙钟误杀。
        var policy = ResolvePolicy(request, options.Value);
        using var timeout = new OperationTimeout(policy.Absolute, policy.Idle, cancellationToken);
        using var process = StartHelperProcess();

        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await FileProtocol.WriteRequestAsync(process.StandardInput.BaseStream, request, timeout.Token);
            if (uploadContent is not null)
            {
                await CopyWithActivityAsync(
                    uploadContent,
                    process.StandardInput.BaseStream,
                    timeout,
                    timeout.Token);
            }

            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();

            FileHelperResponse? response;
            try
            {
                response = await FileProtocol.ReadResponseHeaderAsync(
                    process.StandardOutput.BaseStream,
                    timeout.Token);
            }
            catch (FileProtocolException ex)
            {
                // 帧非法时负载未读，必须终止子进程，否则它会阻塞在写满的管道上。
                Kill(process);
                await WaitForExitAsync(process, timeout.Token);
                logger.LogWarning(ex, "文件辅助进程响应帧非法：op={Op}", request.Op);
                return FileHelperResponse.Failure(
                    FileErrorCodes.HelperFailed,
                    "文件辅助进程返回的数据不合法，操作已终止。");
            }

            if (response is null)
            {
                // 没有读到有效帧：若子进程仍在运行，先终止再等待，避免永久阻塞。
                var wasRunning = !process.HasExited;
                if (wasRunning)
                {
                    Kill(process);
                }

                await WaitForExitAsync(process, timeout.Token);
                if (wasRunning)
                {
                    return FileHelperResponse.Failure(
                        FileErrorCodes.HelperFailed,
                        "文件辅助进程未返回有效响应，操作已终止。");
                }

                return FailureFromExitCode(process.ExitCode, await ReadErrorAsync(errorTask));
            }

            if (response.Ok && downloadOutput is not null)
            {
                if (onDownloadHeader is not null)
                {
                    await onDownloadHeader(response);
                }

                await CopyWithActivityAsync(
                    process.StandardOutput.BaseStream,
                    downloadOutput,
                    timeout,
                    timeout.Token);
                await downloadOutput.FlushAsync(timeout.Token);
            }

            await WaitForExitAsync(process, timeout.Token);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            logger.LogWarning(
                "文件操作超时已终止：op={Op}, path={Path}, elapsed={Elapsed}",
                request.Op,
                request.Path ?? request.Target,
                Stopwatch.GetElapsedTime(startedAt));
            return FileHelperResponse.Failure(FileErrorCodes.Timeout, policy.TimeoutMessage);
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>逐块复制，并在每次成功写入后重设空闲计时（用于上传与下载）。</summary>
    private static async Task CopyWithActivityAsync(
        Stream source,
        Stream destination,
        OperationTimeout timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            timeout.Touch();
        }
    }

    /// <summary>按操作类型选择超时策略与用户可见的错误文案。</summary>
    private static OperationPolicy ResolvePolicy(FileHelperRequest request, FileOptions files)
    {
        var name = OperationName(request.Op);
        return request.Op switch
        {
            "list" => OperationPolicy.CreateAbsolute(
                name,
                files.ListTimeoutSeconds,
                fallbackSeconds: 300,
                "{0}超时（超过 {1} 秒），操作已终止。目录条目过多或文件系统响应缓慢，可稍后重试或进入子目录查看。"),
            "delete" or "copy" or "move" => OperationPolicy.CreateAbsolute(
                name,
                files.TransferTimeoutSeconds,
                fallbackSeconds: 1800,
                "{0}超时（超过 {1} 秒），操作已终止。大目录或跨文件系统操作耗时较长，可分批处理。"),
            "upload" or "download" => OperationPolicy.CreateStream(
                name,
                files.StreamIdleTimeoutSeconds,
                files.StreamMaxSeconds,
                "{0}超时（{1} 秒内没有数据流动），操作已终止。请检查网络连接后重试。"),
            _ => OperationPolicy.CreateAbsolute(
                name,
                files.HelperTimeoutSeconds,
                fallbackSeconds: 120,
                "{0}超时（超过 {1} 秒），操作已终止。"),
        };
    }

    private static string OperationName(string operation) => operation switch
    {
        "list" => "加载目录",
        "mkdir" => "新建目录",
        "touch" => "新建文件",
        "rename" => "重命名",
        "delete" => "删除",
        "copy" => "复制",
        "move" => "移动",
        "upload" => "上传",
        "download" => "下载",
        _ => "文件操作",
    };

    private static Process StartHelperProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // 发布为单文件时可执行文件即本体；dotnet run 时进程是 dotnet，需显式传入程序集路径。
        var processPath = Environment.ProcessPath ?? throw new IOException("无法确定当前可执行文件路径。");
        startInfo.FileName = processPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assemblyPath))
            {
                throw new IOException("无法确定入口程序集路径。");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.ArgumentList.Add(FileHelper.ArgumentName);

        return Process.Start(startInfo) ?? throw new IOException("无法启动文件辅助进程。");
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    private static async Task<string> ReadErrorAsync(Task<string> errorTask)
    {
        try
        {
            return await errorTask;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static FileHelperResponse FailureFromExitCode(int exitCode, string stderr)
    {
        var message = exitCode switch
        {
            FileHelper.InitGroupsFailedExitCode => "无法读取登录用户的补充组，文件操作被拒绝。",
            FileHelper.SetGidFailedExitCode => "无法切换到登录用户的主组，文件操作被拒绝。",
            FileHelper.SetUidFailedExitCode => "无法切换到登录用户身份，文件操作被拒绝。",
            FileHelper.InvalidRequestExitCode => "文件辅助进程收到的请求不合法。",
            FileHelper.UnsupportedExitCode => "文件辅助进程无法启动。",
            _ => $"文件辅助进程异常退出（退出码 {exitCode.ToString(CultureInfo.InvariantCulture)}）。",
        };

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            message += $" {stderr.Trim()}";
        }

        return FileHelperResponse.Failure(FileErrorCodes.HelperFailed, message);
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

    /// <summary>
    /// 一次文件辅助进程调用的超时策略。绝对上限与空闲上限相互独立：
    /// 空闲计时可由数据流动重设，绝对上限一旦到点不受 <see cref="OperationTimeout.Touch"/> 影响。
    /// </summary>
    private sealed record OperationPolicy(
        string OperationName,
        TimeSpan? Absolute,
        TimeSpan? Idle,
        int DisplaySeconds,
        string MessageTemplate)
    {
        public string TimeoutMessage =>
            string.Format(CultureInfo.InvariantCulture, MessageTemplate, OperationName, DisplaySeconds);

        public static OperationPolicy CreateAbsolute(
            string name,
            int seconds,
            int fallbackSeconds,
            string template)
        {
            var value = seconds > 0 ? seconds : fallbackSeconds;
            return new OperationPolicy(name, TimeSpan.FromSeconds(value), null, value, template);
        }

        public static OperationPolicy CreateStream(
            string name,
            int idleSeconds,
            int maxSeconds,
            string template)
        {
            var idle = idleSeconds > 0 ? idleSeconds : 120;
            var absolute = maxSeconds > 0 ? TimeSpan.FromSeconds(maxSeconds) : (TimeSpan?)null;
            return new OperationPolicy(name, absolute, TimeSpan.FromSeconds(idle), idle, template);
        }
    }

    /// <summary>
    /// 组合调用方令牌、绝对上限与空闲上限。空闲计时通过重新排定
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 延后，绝对计时始终保持不变。
    /// </summary>
    private sealed class OperationTimeout : IDisposable
    {
        private readonly CancellationTokenSource? _absolute;
        private readonly CancellationTokenSource? _idle;
        private readonly CancellationTokenSource _linked;
        private readonly TimeSpan _idleDelay;
        private bool _disposed;

        public OperationTimeout(TimeSpan? absolute, TimeSpan? idle, CancellationToken cancellationToken)
        {
            _idleDelay = idle ?? TimeSpan.Zero;
            _absolute = absolute is { } absoluteValue ? new CancellationTokenSource(absoluteValue) : null;
            _idle = idle is { } idleValue ? new CancellationTokenSource(idleValue) : null;

            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _absolute?.Token ?? CancellationToken.None,
                _idle?.Token ?? CancellationToken.None);
        }

        public CancellationToken Token => _linked.Token;

        /// <summary>有数据流动时重设空闲计时。</summary>
        public void Touch()
        {
            if (!_disposed && _idle is not null && !_idle.IsCancellationRequested)
            {
                _idle.CancelAfter(_idleDelay);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _linked.Dispose();
            _idle?.Dispose();
            _absolute?.Dispose();
        }
    }
}
