using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace Koi.Api.Auth;

/// <summary>
/// 通过 P/Invoke 调用 Linux PAM 校验系统账号密码。
/// 注意：pam_unix 通常要求调用进程具备读取 shadow 的权限（root 或 setuid 辅助程序），
/// 部署时应以受控服务账号 + 最小权限方式授予，不应默认以 root 运行。
/// </summary>
public sealed class PamAuthenticator : IAuthenticator
{
    private const string PamLibrary = "libpam.so.0";
    private const int PamSuccess = 0;
    private const int PamConvErr = 19;
    private const int PamPromptEchoOff = 1;
    private const int PamPromptEchoOn = 2;

    private readonly AuthOptions _options;
    private readonly ILogger<PamAuthenticator> _logger;

    public PamAuthenticator(IOptions<AuthOptions> options, ILogger<PamAuthenticator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public AuthenticationResult Authenticate(string userName, string password)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new AuthenticationResult(AuthenticationStatus.PlatformUnavailable, -1, "PAM 认证仅在 Linux 平台可用");
        }

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return new AuthenticationResult(AuthenticationStatus.InvalidCredentials, -1);
        }

        try
        {
            return AuthenticateCore(userName, password);
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "未找到 libpam.so.0，无法执行系统账号认证。");
            return new AuthenticationResult(AuthenticationStatus.PlatformUnavailable, -1, "系统未安装 PAM 运行库");
        }
        catch (EntryPointNotFoundException ex)
        {
            _logger.LogError(ex, "libpam.so.0 缺少必要的认证入口。");
            return new AuthenticationResult(AuthenticationStatus.PlatformUnavailable, -1, "PAM 运行库不完整");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "调用 PAM 认证时发生未预期异常。");
            return new AuthenticationResult(AuthenticationStatus.Error, -1, "认证服务异常");
        }
    }

    private AuthenticationResult AuthenticateCore(string userName, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var passwordPtr = Marshal.AllocHGlobal(passwordBytes.Length + 1);
        Marshal.Copy(passwordBytes, 0, passwordPtr, passwordBytes.Length);
        Marshal.WriteByte(passwordPtr, passwordBytes.Length, 0);

        IntPtr handle = IntPtr.Zero;
        var lastStatus = PamConvErr;
        GCHandle conversationHandle = default;

        try
        {
            // 会话回调：仅对密码提示返回密码副本，其余提示返回空串。
            // 响应内存由 PAM 在调用结束后释放，因此这里不自行释放。
            PamConv conversationCallback = (int numMsg, IntPtr msg, out IntPtr response, IntPtr appData) =>
            {
                response = Marshal.AllocHGlobal(Marshal.SizeOf<PamResponse>() * numMsg);
                for (var i = 0; i < numMsg; i++)
                {
                    var messagePtr = Marshal.ReadIntPtr(msg, i * IntPtr.Size);
                    var message = Marshal.PtrToStructure<PamMessage>(messagePtr);

                    IntPtr answer = IntPtr.Zero;
                    if (message.MsgStyle == PamPromptEchoOff)
                    {
                        answer = Marshal.StringToCoTaskMemUTF8(password);
                    }
                    else if (message.MsgStyle == PamPromptEchoOn)
                    {
                        answer = Marshal.StringToCoTaskMemUTF8(string.Empty);
                    }

                    var pamResponse = new PamResponse { Resp = answer, RespRetCode = IntPtr.Zero };
                    var responsePtr = IntPtr.Add(response, i * Marshal.SizeOf<PamResponse>());
                    Marshal.StructureToPtr(pamResponse, responsePtr, false);
                }

                return PamSuccess;
            };

            // 显式取函数指针并保持委托存活，确保后续 pam_authenticate 调用期间回调有效。
            conversationHandle = GCHandle.Alloc(conversationCallback);
            var conversation = new PamConversation
            {
                Conv = Marshal.GetFunctionPointerForDelegate(conversationCallback),
                AppDataPtr = IntPtr.Zero,
            };

            var startStatus = pam_start(_options.PamService, userName, ref conversation, out handle);
            if (startStatus != PamSuccess)
            {
                _logger.LogWarning("pam_start 失败，状态码 {Status}，服务名 {Service}。", startStatus, _options.PamService);
                return new AuthenticationResult(AuthenticationStatus.Error, startStatus, "PAM 初始化失败");
            }

            lastStatus = pam_authenticate(handle, 0);
            if (lastStatus != PamSuccess)
            {
                return MapFailure(lastStatus, "密码校验");
            }

            lastStatus = pam_acct_mgmt(handle, 0);
            if (lastStatus != PamSuccess)
            {
                return MapFailure(lastStatus, "账号状态校验");
            }

            return new AuthenticationResult(AuthenticationStatus.Success, PamSuccess);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    pam_end(handle, lastStatus);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "pam_end 调用失败。");
                }
            }

            if (conversationHandle.IsAllocated)
            {
                conversationHandle.Free();
            }

            // 清除进程内保存的密码副本。
            for (var i = 0; i < passwordBytes.Length; i++)
            {
                Marshal.WriteByte(passwordPtr, i, 0);
            }

            Marshal.FreeHGlobal(passwordPtr);
        }
    }

    private AuthenticationResult MapFailure(int status, string stage)
    {
        // PAM_AUTH_ERR(7)、PAM_USER_UNKNOWN(10)、PAM_PERM_DENIED(6)、PAM_MAXTRIES(11)、
        // PAM_NEW_AUTHTOK_REQD(12)、PAM_ACCT_EXPIRED(13) 一律按凭证或账号不可用处理，
        // 对外提示保持一致，避免泄露账号是否存在。
        if (status is 6 or 7 or 10 or 11 or 12 or 13)
        {
            return new AuthenticationResult(AuthenticationStatus.InvalidCredentials, status);
        }

        _logger.LogWarning("PAM {Stage} 失败，状态码 {Status}。", stage, status);
        return new AuthenticationResult(AuthenticationStatus.Error, status, $"PAM {stage}失败");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamMessage
    {
        public int MsgStyle;
        public IntPtr Msg;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamResponse
    {
        public IntPtr Resp;
        public IntPtr RespRetCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamConversation
    {
        public IntPtr Conv;
        public IntPtr AppDataPtr;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PamConv(int numMsg, IntPtr msg, out IntPtr response, IntPtr appDataPtr);

    [DllImport(PamLibrary, EntryPoint = "pam_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
        ref PamConversation conversation,
        out IntPtr handle);

    [DllImport(PamLibrary, EntryPoint = "pam_authenticate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_authenticate(IntPtr handle, int flags);

    [DllImport(PamLibrary, EntryPoint = "pam_acct_mgmt", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_acct_mgmt(IntPtr handle, int flags);

    [DllImport(PamLibrary, EntryPoint = "pam_end", CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_end(IntPtr handle, int status);
}
