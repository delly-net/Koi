using System.Runtime.InteropServices;

namespace Koi.Api.Files;

/// <summary>
/// 文件辅助进程模式：主进程以隐藏参数拉起本程序，本模式从标准输入读取一次操作请求，
/// 降权到登录用户后执行文件操作，再把响应写回标准输出。
/// </summary>
/// <remarks>
/// 与终端辅助进程同理，这里是**独立进程**（由 <see cref="System.Diagnostics.Process"/> 原生 fork/exec 创建），
/// 不涉及在托管进程内 fork；降权后继续执行托管代码是安全的。
/// 请求/响应协议见 <see cref="FileProtocol"/>：4 字节长度 + JSON 头，上传/下载时头后紧跟原始字节。
/// </remarks>
public static class FileHelper
{
    /// <summary>隐藏模式标记：作为进程首个参数出现时进入辅助逻辑。</summary>
    public const string ArgumentName = "--koi-file-helper";

    /// <summary>无法切换到登录用户的主组。</summary>
    public const int SetGidFailedExitCode = 111;

    /// <summary>无法读取登录用户的补充组。</summary>
    public const int InitGroupsFailedExitCode = 112;

    /// <summary>无法切换为登录用户身份。</summary>
    public const int SetUidFailedExitCode = 113;

    /// <summary>请求无法解析。</summary>
    public const int InvalidRequestExitCode = 120;

    /// <summary>非 Linux 平台或发生未预期异常。</summary>
    public const int UnsupportedExitCode = 127;

    /// <summary>
    /// 参数以辅助模式标记开头时执行辅助逻辑（正常不会返回）；否则返回 <c>false</c>，
    /// 调用方按普通 Web 服务启动。
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], ArgumentName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            Environment.Exit(UnsupportedExitCode);
        }

        RunAsync().GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync()
    {
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();

        FileHelperRequest? request;
        try
        {
            request = await FileProtocol.ReadRequestAsync(input, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or FileProtocolException)
        {
            request = null;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserName))
        {
            Environment.Exit(InvalidRequestExitCode);
            return;
        }

        // 降权顺序固定：initgroups 必须在 setuid 之前（降权后读不到补充组），setgid 先于 setuid。
        if (initgroups(request.UserName, request.Gid) != 0)
        {
            Environment.Exit(InitGroupsFailedExitCode);
        }

        if (setgid(request.Gid) != 0)
        {
            Environment.Exit(SetGidFailedExitCode);
        }

        if (setuid(request.Uid) != 0)
        {
            Environment.Exit(SetUidFailedExitCode);
        }

        // 切到根目录，避免工作目录属于不可访问路径。
        chdir("/");

        try
        {
            if (request.Op is "download" or "readtext")
            {
                var (response, stream) = FileOperations.OpenDownload(request);
                using (stream)
                {
                    await FileProtocol.WriteResponseHeaderAsync(output, response, CancellationToken.None);
                    if (stream is not null)
                    {
                        await stream.CopyToAsync(output, 81920, CancellationToken.None);
                        await output.FlushAsync(CancellationToken.None);
                    }
                }

                return;
            }

            var result = await FileOperations.ExecuteAsync(request, input, CancellationToken.None);
            await FileProtocol.WriteResponseHeaderAsync(output, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            try
            {
                var failure = FileHelperResponse.Failure(FileErrorCodes.IoError, ex.Message);
                await FileProtocol.WriteResponseHeaderAsync(output, failure, CancellationToken.None);
            }
            catch (IOException)
            {
                Environment.Exit(UnsupportedExitCode);
            }
        }
    }

    [DllImport("libc", EntryPoint = "initgroups", SetLastError = true)]
    private static extern int initgroups(string userName, uint gid);

    [DllImport("libc", EntryPoint = "setgid", SetLastError = true)]
    private static extern int setgid(uint gid);

    [DllImport("libc", EntryPoint = "setuid", SetLastError = true)]
    private static extern int setuid(uint uid);

    [DllImport("libc", EntryPoint = "chdir", SetLastError = true)]
    private static extern int chdir(string path);
}
