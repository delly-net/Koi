namespace Koi.Api.Docker;

/// <summary>
/// 容器管理配置的规范化与校验。配置来自界面输入，必须由服务端做范围与字符检查。
/// </summary>
public static class DockerSettingsValidator
{
    private const int MaxGroupNameLength = 64;
    private const int MaxPathLength = 4096;
    private const long MinByteLimit = 1024;
    private const long MaxByteLimit = 256L * 1024 * 1024;
    private const int MaxLogTail = 100000;

    /// <summary>规范化：docker 路径去首尾空白，允许组去空白、去重。</summary>
    public static DockerOptions Normalize(DockerOptions settings)
    {
        settings.DockerPath = settings.DockerPath?.Trim() ?? string.Empty;
        settings.AllowedGroups = (settings.AllowedGroups ?? [])
            .Select(group => group?.Trim() ?? string.Empty)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(DockerOptions settings)
    {
        if (settings.DockerPath.Length > MaxPathLength)
        {
            return $"docker 路径不能超过 {MaxPathLength} 个字符。";
        }

        if (settings.DockerPath.Any(char.IsControl))
        {
            return "docker 路径不能包含控制字符。";
        }

        if (settings.DockerPath.Length > 0 && !Path.IsPathRooted(settings.DockerPath))
        {
            return "docker 路径必须是绝对路径，或留空以便自动探测。";
        }

        foreach (var group in settings.AllowedGroups ?? [])
        {
            if (group.Length > MaxGroupNameLength ||
                group.Any(char.IsWhiteSpace) ||
                group.Any(char.IsControl) ||
                group.Contains(':'))
            {
                return $"用户组名“{group}”不合法：不能超过 {MaxGroupNameLength} 个字符，且不能包含空白、冒号或控制字符。";
            }
        }

        if (settings.CommandTimeoutSeconds is < 1 or > 86400)
        {
            return "命令超时必须在 1 到 86400 秒之间。";
        }

        if (settings.PullTimeoutSeconds is < 1 or > 86400)
        {
            return "镜像拉取超时必须在 1 到 86400 秒之间。";
        }

        if (settings.MaxOutputBytes is < MinByteLimit or > MaxByteLimit)
        {
            return "命令输出上限必须在 1 KiB 到 256 MiB 之间。";
        }

        if (settings.MaxLogBytes is < MinByteLimit or > MaxByteLimit)
        {
            return "容器日志上限必须在 1 KiB 到 256 MiB 之间。";
        }

        if (settings.LogTailDefault is < 1 or > MaxLogTail)
        {
            return $"日志默认行数必须在 1 到 {MaxLogTail} 之间。";
        }

        if (settings.LogTailMax is < 1 or > MaxLogTail)
        {
            return $"日志行数上限必须在 1 到 {MaxLogTail} 之间。";
        }

        if (settings.LogTailDefault > settings.LogTailMax)
        {
            return "日志默认行数不能大于日志行数上限。";
        }

        return null;
    }
}
