using System.ComponentModel.DataAnnotations;

namespace Koi.Api.Data;

/// <summary>
/// 通用功能配置。每个功能一行，配置内容以 JSON 保存，
/// 「功能」字段用于区分不同功能的配置，后续新增功能可复用同一张表。
/// </summary>
public class FeatureConfigEntity
{
    public long Id { get; set; }

    /// <summary>功能标识（例如 <c>packages</c>），全局唯一。</summary>
    [MaxLength(64)]
    public string Feature { get; set; } = string.Empty;

    /// <summary>该功能的配置内容（UTF-8 JSON）。</summary>
    public string ConfigJson { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最后修改配置的登录账号。</summary>
    [MaxLength(64)]
    public string? UpdatedBy { get; set; }
}
