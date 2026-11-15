namespace Koi.Api.Files;

/// <summary>
/// 文件管理配置的规范化与校验。配置来自界面输入，必须由服务端做路径与范围检查。
/// </summary>
public static class FileSettingsValidator
{
    /// <summary>允许配置的根目录数量上限。</summary>
    private const int MaxAllowedRoots = 64;

    /// <summary>在线编辑文本大小上限的硬顶（64 MiB）。</summary>
    private const long MaxTextFileBytesCeiling = 64L * 1024 * 1024;

    /// <summary>单次目录列举条目上限的硬顶。</summary>
    private const int MaxListEntriesCeiling = 100_000;

    /// <summary>规范化：根目录去空白、规范化为绝对路径并去重，其余字段原样返回。</summary>
    public static FileOptions Normalize(FileOptions settings)
    {
        settings.AllowedRoots = (settings.AllowedRoots ?? [])
            .Select(root => root?.Trim() ?? string.Empty)
            .Where(root => root.Length > 0)
            .Select(root => FilePaths.TryNormalize(root, out var normalized) ? normalized : root)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(FileOptions settings)
    {
        var roots = settings.AllowedRoots ?? [];
        if (roots.Length == 0)
        {
            return "至少需要配置一个允许访问的根目录。";
        }

        if (roots.Length > MaxAllowedRoots)
        {
            return $"允许访问的根目录最多 {MaxAllowedRoots} 个。";
        }

        foreach (var root in roots)
        {
            if (!FilePaths.TryNormalize(root, out _))
            {
                return $"根目录“{root}”不合法：必须是绝对路径，且不能包含控制字符或超过 {FilePaths.MaxPathLength} 个字符。";
            }
        }

        if (settings.MaxUploadBytes is < 1 or > FileOptions.MaxUploadBytesCeiling)
        {
            return $"单个上传文件上限必须在 1 字节到 {FileOptions.MaxUploadBytesCeiling / (1024 * 1024)} MiB 之间。";
        }

        if (settings.MaxListEntries is < 1 or > MaxListEntriesCeiling)
        {
            return $"目录列举条目上限必须在 1 到 {MaxListEntriesCeiling} 之间。";
        }

        if (settings.MaxTextFileBytes is < 1 or > MaxTextFileBytesCeiling)
        {
            return $"在线编辑文本上限必须在 1 字节到 {MaxTextFileBytesCeiling / (1024 * 1024)} MiB 之间。";
        }

        if (settings.HelperTimeoutSeconds is < 1 or > 3600)
        {
            return "普通操作超时必须在 1 到 3600 秒之间。";
        }

        if (settings.ListTimeoutSeconds is < 1 or > 3600)
        {
            return "目录列举超时必须在 1 到 3600 秒之间。";
        }

        if (settings.TransferTimeoutSeconds is < 1 or > 86400)
        {
            return "删除/复制/移动超时必须在 1 到 86400 秒之间。";
        }

        if (settings.StreamIdleTimeoutSeconds is < 1 or > 3600)
        {
            return "上传/下载空闲超时必须在 1 到 3600 秒之间。";
        }

        if (settings.StreamMaxSeconds is < 0 or > 86400)
        {
            return "上传/下载总时长上限必须在 0 到 86400 秒之间（0 表示不限制）。";
        }

        return null;
    }
}
