namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑后缀规则与着色方案的规范化与校验。规则来自界面输入，必须由服务端再次检查。
/// </summary>
public static class EditorFileTypeValidator
{
    /// <summary>允许配置的规则数量上限。</summary>
    private const int MaxFileTypes = 500;

    /// <summary>单条后缀/文件名规则的最大长度。</summary>
    private const int MaxRuleLength = 64;

    /// <summary>
    /// 规范化：去空白、转小写、去重保序、丢弃空项，方案统一小写；空数组是合法值，
    /// 表示文件管理中不把任何文件视为文本，编辑器也就没有可着色的后缀。
    /// </summary>
    public static List<EditorFileTypeInfo> Normalize(IEnumerable<EditorFileTypeInfo?>? items)
    {
        var result = new List<EditorFileTypeInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items ?? [])
        {
            var extension = item?.Extension?.Trim().ToLowerInvariant() ?? string.Empty;
            if (extension.Length == 0 || !seen.Add(extension))
            {
                continue;
            }

            var scheme = EditorSyntaxSchemes.Normalize(item?.Scheme);
            result.Add(new EditorFileTypeInfo(extension, scheme));
        }

        return result;
    }

    /// <summary>校验规则；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(IReadOnlyList<EditorFileTypeInfo> items)
    {
        if (items.Count > MaxFileTypes)
        {
            return $"文本编辑后缀规则最多 {MaxFileTypes} 条。";
        }

        foreach (var item in items)
        {
            var extension = item.Extension;
            if (extension.Length is < 1 or > MaxRuleLength)
            {
                return $"规则“{extension}”不合法：长度必须在 1 到 {MaxRuleLength} 个字符之间。";
            }

            if (extension.IndexOfAny(['/', '\\', '*', '?']) >= 0
                || extension.Any(char.IsWhiteSpace)
                || extension.Any(char.IsControl))
            {
                return $"规则“{extension}”不合法：不能包含路径分隔符、通配符、空白或控制字符。";
            }

            if (!EditorSyntaxSchemes.IsKnown(item.Scheme))
            {
                return $"规则“{extension}”的着色方案“{item.Scheme}”不合法：必须是内置方案之一（{string.Join("、", EditorSyntaxSchemes.All)}）。";
            }
        }

        return null;
    }
}
