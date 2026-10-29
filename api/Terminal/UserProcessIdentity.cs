using System.Runtime.InteropServices;

namespace Koi.Api.Terminal;

/// <summary>系统账号信息，用于把终端子进程降权到登录用户。</summary>
public sealed record UserProcessIdentity(
    string UserName,
    uint Uid,
    uint Gid,
    string HomeDirectory,
    string Shell);

/// <summary>通过 <c>getpwnam_r</c> 读取本地系统账号信息（终端降权依赖）。</summary>
public static class UserProcessIdentityResolver
{
    private const int BufferSize = 8192;
    private const int MaxUserNameLength = 64;

    /// <summary>
    /// 解析登录账号；账号不存在、用户名非法或平台不匹配时返回 <c>null</c>。
    /// shell 优先级：配置值 → 账号登录 shell → /bin/sh。
    /// </summary>
    public static UserProcessIdentity? TryResolve(string userName, string? configuredShell)
    {
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(userName) ||
            userName.Length > MaxUserNameLength ||
            userName.Contains(':') ||
            userName.Contains('\0') ||
            userName.Any(char.IsControl))
        {
            return null;
        }

        var namePtr = Marshal.StringToCoTaskMemUTF8(userName);
        var passwdPtr = Marshal.AllocHGlobal(64);
        var bufferPtr = Marshal.AllocHGlobal(BufferSize);
        try
        {
            var status = getpwnam_r(namePtr, passwdPtr, bufferPtr, (nuint)BufferSize, out var result);
            if (status != 0 || result == IntPtr.Zero)
            {
                return null;
            }

            // struct passwd（glibc x86_64/aarch64）：
            // pw_name(0) pw_passwd(8) pw_uid(16) pw_gid(20) pw_gecos(24) pw_dir(32) pw_shell(40)
            var uid = (uint)Marshal.ReadInt32(result, 16);
            var gid = (uint)Marshal.ReadInt32(result, 20);
            var home = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result, 32));
            var passwdShell = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result, 40));

            var shell = ResolveShell(configuredShell, passwdShell);
            var homeDirectory = string.IsNullOrWhiteSpace(home) ? "/" : home;

            return new UserProcessIdentity(userName, uid, gid, homeDirectory, shell);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            Marshal.FreeHGlobal(passwdPtr);
            Marshal.FreeHGlobal(bufferPtr);
        }
    }

    private static string ResolveShell(string? configuredShell, string? passwdShell)
    {
        if (IsUsableShell(configuredShell))
        {
            return configuredShell!;
        }

        if (IsUsableShell(passwdShell))
        {
            return passwdShell!;
        }

        return "/bin/sh";
    }

    private static bool IsUsableShell(string? shell) =>
        !string.IsNullOrWhiteSpace(shell) &&
        shell.StartsWith('/') &&
        !shell.Contains('\0') &&
        File.Exists(shell);

    [DllImport("libc", EntryPoint = "getpwnam_r", SetLastError = true)]
    private static extern int getpwnam_r(IntPtr name, IntPtr passwd, IntPtr buffer, nuint bufferLength, out IntPtr result);
}
