using System.ComponentModel.DataAnnotations;

namespace Koi.Api.Data;

/// <summary>
/// 文本编辑支持的文件后缀名及其着色方案。
/// </summary>
/// <remarks>
/// 每条记录表示一个可在「文件管理」中作为文本打开、并在编辑器里按
/// <see cref="Scheme"/> 着色的后缀/文件名规则。该表取代了原先存放在
/// <c>FeatureConfigs(Feature = editor)</c> 中的 <c>textExtensions</c>，
/// 成为文本关联与编辑器着色的单一来源。
/// </remarks>
public class EditorFileTypeEntity
{
    public long Id { get; set; }

    /// <summary>后缀或文件名规则（统一小写），全局唯一。</summary>
    [MaxLength(64)]
    public string Extension { get; set; } = string.Empty;

    /// <summary>着色方案标识，取值见 <c>EditorSyntaxSchemes</c>。</summary>
    [MaxLength(32)]
    public string Scheme { get; set; } = "plaintext";

    /// <summary>最近一次写入时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最近一次写入的账号。</summary>
    [MaxLength(64)]
    public string? UpdatedBy { get; set; }
}
