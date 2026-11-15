namespace Koi.Api.Processes;

/// <summary>
/// 进程管理配置的规范化与校验。配置来自界面输入，必须由服务端做范围与字符检查。
/// </summary>
public static class ProcessSettingsValidator
{
    private const int MaxGroupNameLength = 64;

    /// <summary>规范化：允许组去空白、去空、去重。</summary>
    public static ProcessOptions Normalize(ProcessOptions settings)
    {
        settings.AllowedGroups = (settings.AllowedGroups ?? [])
            .Select(group => group?.Trim() ?? string.Empty)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(ProcessOptions settings)
    {
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

        if (settings.MaxProcesses is < 100 or > 20000)
        {
            return "最大进程数必须在 100 到 20000 之间。";
        }

        if (settings.MaxCommandLineLength is < 64 or > 4096)
        {
            return "命令行长度上限必须在 64 到 4096 个字符之间。";
        }

        if (settings.SampleWindowMilliseconds is < 50 or > 5000)
        {
            return "采样窗口必须在 50 到 5000 毫秒之间。";
        }

        if (settings.TerminateTimeoutSeconds is < 1 or > 60)
        {
            return "结束进程等待时间必须在 1 到 60 秒之间。";
        }

        if (settings.RefreshIntervalSeconds is < 1 or > 60)
        {
            return "自动刷新间隔必须在 1 到 60 秒之间。";
        }

        return null;
    }
}
