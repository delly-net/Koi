namespace Koi.Api.Nginx;

/// <summary>
/// Nginx 管理配置的规范化与校验。配置来自界面输入，必须由服务端做范围与字符检查。
/// </summary>
public static class NginxSettingsValidator
{
    private const int MaxGroupNameLength = 64;
    private const int MaxPathLength = 4096;
    private const long MinByteLimit = 1024;
    private const long MaxByteLimit = 16L * 1024 * 1024;
    private const int MaxListEntries = 10000;

    /// <summary>规范化：路径去首尾空白，允许组去空白、去重。</summary>
    public static NginxOptions Normalize(NginxOptions settings)
    {
        settings.NginxPath = settings.NginxPath?.Trim() ?? string.Empty;
        settings.ConfDirectory = settings.ConfDirectory?.Trim() ?? string.Empty;
        settings.MainConfigPath = settings.MainConfigPath?.Trim() ?? string.Empty;
        settings.AllowedGroups = (settings.AllowedGroups ?? [])
            .Select(group => group?.Trim() ?? string.Empty)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(NginxOptions settings)
    {
        if (ValidateOptionalPath(settings.NginxPath, "nginx 路径") is { } nginxError)
        {
            return nginxError;
        }

        if (ValidateRequiredDirectory(settings.ConfDirectory, "conf.d 目录") is { } directoryError)
        {
            return directoryError;
        }

        if (ValidateOptionalPath(settings.MainConfigPath, "主配置路径") is { } configError)
        {
            return configError;
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

        if (settings.CommandTimeoutSeconds is < 1 or > 3600)
        {
            return "命令超时必须在 1 到 3600 秒之间。";
        }

        if (settings.MaxOutputBytes is < MinByteLimit or > MaxByteLimit)
        {
            return "命令输出上限必须在 1 KiB 到 16 MiB 之间。";
        }

        if (settings.MaxFileBytes is < MinByteLimit or > MaxByteLimit)
        {
            return "配置文件大小上限必须在 1 KiB 到 16 MiB 之间。";
        }

        if (settings.MaxListEntries is < 1 or > MaxListEntries)
        {
            return $"文件清单条目上限必须在 1 到 {MaxListEntries} 之间。";
        }

        return null;
    }

    /// <summary>空串表示自动探测；非空必须是绝对路径，且不含控制字符。</summary>
    private static string? ValidateOptionalPath(string path, string label)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return ValidateAbsolutePath(path, label);
    }

    private static string? ValidateRequiredDirectory(string path, string label)
    {
        if (string.IsNullOrEmpty(path))
        {
            return $"{label}不能为空。";
        }

        return ValidateAbsolutePath(path, label);
    }

    private static string? ValidateAbsolutePath(string path, string label)
    {
        if (path.Length > MaxPathLength)
        {
            return $"{label}不能超过 {MaxPathLength} 个字符。";
        }

        if (path.Any(char.IsControl))
        {
            return $"{label}不能包含控制字符。";
        }

        if (!Path.IsPathRooted(path))
        {
            return $"{label}必须是绝对路径。";
        }

        return null;
    }
}
