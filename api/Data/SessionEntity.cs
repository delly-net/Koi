using System.ComponentModel.DataAnnotations;

namespace Koi.Api.Data;

/// <summary>
/// 登录会话。数据库只保存会话令牌的 SHA-256 摘要，避免数据库泄露时会话被直接冒用。
/// </summary>
public class SessionEntity
{
    public long Id { get; set; }

    /// <summary>会话令牌的 SHA-256 十六进制摘要。</summary>
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Linux 系统账号名。</summary>
    [MaxLength(64)]
    public string UserName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    [MaxLength(64)]
    public string? RemoteIp { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }
}
