namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑配置的规范化与校验。扩展名规则来自界面输入，必须由服务端再次检查。
/// </summary>
public static class EditorSettingsValidator
{
    /// <summary>允许配置的规则数量上限。</summary>
    private const int MaxTextExtensions = 500;

    /// <summary>单条规则的最大长度。</summary>
    private const int MaxRuleLength = 64;

    /// <summary>
    /// 规范化：去空白、转小写、去重保序、丢弃空项。
    /// 空数组是合法值，表示文件管理中不把任何文件视为文本。
    /// </summary>
    public static EditorOptions Normalize(EditorOptions settings)
    {
        var result = new List<string>();
        foreach (var item in settings.TextExtensions ?? [])
        {
            var value = item?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Length == 0 || result.Contains(value, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(value);
        }

        settings.TextExtensions = [.. result];
        return settings;
    }

    /// <summary>校验配置；返回 <c>null</c> 表示合法，否则返回中文错误详情。</summary>
    public static string? Validate(EditorOptions settings)
    {
        var rules = settings.TextExtensions ?? [];
        if (rules.Length > MaxTextExtensions)
        {
            return $"文本文件扩展名规则最多 {MaxTextExtensions} 条。";
        }

        foreach (var rule in rules)
        {
            if (rule.Length is < 1 or > MaxRuleLength)
            {
                return $"规则“{rule}”不合法：长度必须在 1 到 {MaxRuleLength} 个字符之间。";
            }

            if (rule.IndexOfAny(['/', '\\', '*', '?']) >= 0 || rule.Any(char.IsWhiteSpace) || rule.Any(char.IsControl))
            {
                return $"规则“{rule}”不合法：不能包含路径分隔符、通配符、空白或控制字符。";
            }
        }

        return null;
    }
}
