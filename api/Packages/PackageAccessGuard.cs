using System.ComponentModel;
using System.Diagnostics;
using Koi.Api.Terminal;

namespace Koi.Api.Packages;

/// <summary>
/// 应用管理的公共前置校验：开关、平台、包管理器可用性、登录账号与授权组。
/// </summary>
/// <remarks>
/// 包管理操作等同于宿主 root，因此不能仅凭「已登录」放行。组成员通过
/// <c>id -nG -- &lt;user&gt;</c> 读取（尊重 NSS/LDAP），失败按 503 拒绝而不是默认放行。
/// uid 0 的登录账号不受 <see cref="PackageOptions.AllowedGroups"/> 限制。
/// 写操作（安装/卸载/升级/源变更）还需 <see cref="IsServiceRoot"/> 为 true。
/// </remarks>
public static class PackageAccessGuard
{
    private const int MaxUserNameLength = 64;
    private static readonly TimeSpan IdentityCommandTimeout = TimeSpan.FromSeconds(5);

    public static async Task<(IResult? Error, string UserName, bool IsRoot)> AuthorizeAsync(
        HttpContext context,
        PackageOptions settings,
        PackageManagerResolver resolver,
        CancellationToken cancellationToken)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!settings.Enabled)
        {
            return (Problem(StatusCodes.Status503ServiceUnavailable, "应用管理已关闭", "管理员已禁用应用管理功能。"), userName, false);
        }

        if (!OperatingSystem.IsLinux())
        {
            return (Problem(
                StatusCodes.Status503ServiceUnavailable,
                "应用管理不可用",
                "应用管理依赖 Linux 包管理器，当前平台不支持。"), userName, false);
        }

        if (resolver.Backend is null)
        {
            return (Problem(
                StatusCodes.Status503ServiceUnavailable,
                "包管理器不可用",
                "未找到受支持的包管理器（apt/dnf/yum/pacman/zypper），请安装或在应用管理设置中指定包管理器。"), userName, false);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Problem(StatusCodes.Status401Unauthorized, "未登录", "请先登录后再访问应用管理。"), userName, false);
        }

        if (!IsValidUserName(userName))
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无法识别账号",
                "当前登录账号名不合法，无法校验应用管理权限。"), userName, false);
        }

        if (IsRootAccount(userName))
        {
            return (null, userName, true);
        }

        if (settings.AllowedGroups is not { Length: > 0 })
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无权访问应用管理",
                "管理员未配置允许访问应用管理的用户组。"), userName, false);
        }

        var (groups, error) = await ReadGroupsAsync(userName, cancellationToken);
        if (groups is null)
        {
            return (Problem(StatusCodes.Status503ServiceUnavailable, "无法校验应用管理权限", error), userName, false);
        }

        if (!groups.Any(group => settings.AllowedGroups.Contains(group, StringComparer.Ordinal)))
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无权访问应用管理",
                $"当前账号不在允许的应用管理组（{string.Join("、", settings.AllowedGroups)}）内。"), userName, false);
        }

        return (null, userName, false);
    }

    /// <summary>
    /// 登录账号是否为 uid 0。必须经 <c>getpwnam_r</c> 解析，不能按用户名比较。
    /// </summary>
    public static bool IsRootAccount(string userName) =>
        !string.IsNullOrEmpty(userName) &&
        IsValidUserName(userName) &&
        UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is { Uid: 0 };

    /// <summary>
    /// Koi 服务进程是否以 root 运行。包管理器命令由服务进程执行，
    /// 非 root 时只能查询、不能安装/卸载/修改软件源。
    /// </summary>
    public static bool IsServiceRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name) || !IsValidUserName(name))
        {
            return false;
        }

        return UserProcessIdentityResolver.TryResolve(name, configuredShell: null) is { Uid: 0 };
    }

    private static bool IsValidUserName(string userName) =>
        userName.Length <= MaxUserNameLength &&
        !userName.Contains(':') &&
        !userName.Contains('\0') &&
        !userName.Any(char.IsControl);

    /// <summary>读取账号的全部组名；失败返回 <c>(null, 原因)</c>。</summary>
    private static async Task<(string[]? Groups, string Error)> ReadGroupsAsync(
        string userName,
        CancellationToken cancellationToken)
    {
        var idPath = File.Exists("/usr/bin/id") ? "/usr/bin/id" : "/bin/id";
        if (!File.Exists(idPath))
        {
            return (null, "系统缺少 id 命令，无法校验应用管理权限。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = idPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // 用户名不作为 shell 解析，且用 -- 终止选项，避免以 - 开头的名称被当作参数。
        startInfo.ArgumentList.Add("-nG");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(userName);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Process.Start 返回 null。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (null, $"无法执行 id 命令：{ex.Message}");
        }

        using (process)
        {
            process.StandardInput.Close();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(IdentityCommandTimeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                return (null, $"读取账号 {userName} 的组信息超时。");
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
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"退出码 {process.ExitCode}" : stderr.Trim();
                return (null, $"无法读取账号 {userName} 的组信息：{detail}");
            }

            return (stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), string.Empty);
        }
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

    private static IResult Problem(int statusCode, string title, string detail) =>
        Results.Problem(statusCode: statusCode, title: title, detail: detail);
}
