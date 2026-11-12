using System.ComponentModel;
using System.Diagnostics;

namespace Koi.Api.Docker;

/// <summary>
/// 容器管理的公共前置校验：开关、平台、docker 可用性、登录账号与 docker 组成员资格。
/// </summary>
/// <remarks>
/// Docker socket 的访问权限等同于宿主 root，因此不能仅凭「已登录」放行。
/// 组成员通过系统 <c>id -nG -- &lt;user&gt;</c> 读取（尊重 NSS/LDAP 配置），
/// 无法读取时按 503 拒绝而不是默认放行。
/// </remarks>
public static class DockerAccessGuard
{
    private const int MaxUserNameLength = 64;
    private static readonly TimeSpan IdentityCommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>执行前置校验；返回 <c>null</c> 表示允许访问。</summary>
    public static async Task<(IResult? Error, string UserName)> AuthorizeAsync(
        HttpContext context,
        DockerOptions settings,
        CancellationToken cancellationToken)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!settings.Enabled)
        {
            return (Problem(StatusCodes.Status503ServiceUnavailable, "容器管理已关闭", "管理员已禁用容器管理功能。"), userName);
        }

        if (!OperatingSystem.IsLinux())
        {
            return (Problem(
                StatusCodes.Status503ServiceUnavailable,
                "容器管理不可用",
                "容器管理依赖 Linux 上的 Docker，当前平台不支持。"), userName);
        }

        if (!DockerCommandRunner.IsAvailable(settings))
        {
            return (Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Docker 不可用",
                "未找到 docker 可执行文件，请安装 Docker 或在 Koi:Docker:DockerPath 中配置路径。"), userName);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Problem(StatusCodes.Status401Unauthorized, "未登录", "请先登录后再访问容器管理。"), userName);
        }

        if (!IsValidUserName(userName))
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无法识别账号",
                "当前登录账号名不合法，无法校验 Docker 访问权限。"), userName);
        }

        if (settings.AllowedGroups is not { Length: > 0 })
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无权访问容器管理",
                "管理员未配置允许访问 Docker 的用户组。"), userName);
        }

        var (groups, error) = await ReadGroupsAsync(userName, cancellationToken);
        if (groups is null)
        {
            return (Problem(StatusCodes.Status503ServiceUnavailable, "无法校验 Docker 权限", error), userName);
        }

        if (!groups.Any(group => settings.AllowedGroups.Contains(group, StringComparer.Ordinal)))
        {
            return (Problem(
                StatusCodes.Status403Forbidden,
                "无权访问容器管理",
                $"当前账号不在允许的 Docker 组（{string.Join("、", settings.AllowedGroups)}）内。"), userName);
        }

        return (null, userName);
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
            return (null, "系统缺少 id 命令，无法校验 Docker 访问权限。");
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
