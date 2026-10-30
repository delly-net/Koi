using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Koi.Api.Files;

/// <summary>
/// 主进程侧的文件操作入口：以隐藏参数拉起文件辅助进程，写入请求并读取响应。
/// 文件读写全部发生在降权后的辅助进程内，主进程（可能以 root 运行）不直接触碰文件系统。
/// </summary>
public sealed class FileOperationService(IOptions<FileOptions> options)
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

        using var process = StartHelperProcess();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.Value.HelperTimeoutSeconds)));

        var errorTask = process.StandardError.ReadToEndAsync(lifetime.Token);

        try
        {
            await FileProtocol.WriteRequestAsync(process.StandardInput.BaseStream, request, lifetime.Token);
            if (uploadContent is not null)
            {
                await uploadContent.CopyToAsync(process.StandardInput.BaseStream, CopyBufferSize, lifetime.Token);
            }

            await process.StandardInput.BaseStream.FlushAsync(lifetime.Token);
            process.StandardInput.Close();

            var response = await FileProtocol.ReadResponseHeaderAsync(
                process.StandardOutput.BaseStream,
                lifetime.Token);

            if (response is null)
            {
                await WaitForExitAsync(process, lifetime.Token);
                return FailureFromExitCode(process.ExitCode, await ReadErrorAsync(errorTask));
            }

            if (response.Ok && downloadOutput is not null)
            {
                if (onDownloadHeader is not null)
                {
                    await onDownloadHeader(response);
                }

                await process.StandardOutput.BaseStream.CopyToAsync(
                    downloadOutput,
                    CopyBufferSize,
                    lifetime.Token);
                await downloadOutput.FlushAsync(lifetime.Token);
            }

            await WaitForExitAsync(process, lifetime.Token);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return FileHelperResponse.Failure(FileErrorCodes.Timeout, "文件操作超时，请稍后重试。");
        }
        finally
        {
            Kill(process);
        }
    }

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
}
