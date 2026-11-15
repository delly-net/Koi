namespace Koi.Api.Packages;

/// <summary>
/// 应用管理配置的规范化与校验。配置来自界面输入，必须由服务端做白名单与范围检查。
/// </summary>
public static class PackageSettingsValidator
{
    private static readonly string[] SupportedManagers = ["", "apt", "dnf", "yum", "pacman", "zypper"];

    private const int MaxGroupNameLength = 64;
    private const long MaxByteLimit = 256L * 1024 * 1024;

    /// <summary>规范化：包管理器名统一小写去空格，允许组去空白、去重。</summary>
    public static PackageOptions Normalize(PackageOptions settings)
    {
        settings.Manager = settings.Manager?.Trim().ToLowerInvariant() ?? string.Empty;
        settings.AllowedGroups = (settings.AllowedGroups ?? [])
            .Select(group => group?.Trim() ?? string.Empty)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(PackageOptions settings)
    {
        if (!SupportedManagers.Contains(settings.Manager, StringComparer.Ordinal))
        {
            return $"包管理器只能为空（自动检测）或 {string.Join(" / ", SupportedManagers.Skip(1))}。";
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
            return "查询超时必须在 1 到 86400 秒之间。";
        }

        if (settings.JobTimeoutSeconds is < 1 or > 172800)
        {
            return "任务超时必须在 1 到 172800 秒之间。";
        }

        if (settings.MaxOutputBytes is < 1024 or > MaxByteLimit)
        {
            return "查询输出上限必须在 1 KiB 到 256 MiB 之间。";
        }

        if (settings.MaxLogBytes is < 1024 or > MaxByteLimit)
        {
            return "任务日志上限必须在 1 KiB 到 256 MiB 之间。";
        }

        if (settings.JobRetentionMinutes is < 1 or > 10080)
        {
            return "任务保留时间必须在 1 到 10080 分钟之间。";
        }

        if (settings.MaxPackagesPerJob is < 1 or > 1000)
        {
            return "单个任务的目标包数量必须在 1 到 1000 之间。";
        }

        if (settings.MaxInstalledEntries is < 1 or > 1000000)
        {
            return "已安装列表上限必须在 1 到 1000000 之间。";
        }

        return null;
    }
}
