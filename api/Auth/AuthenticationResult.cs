namespace Koi.Api.Auth;

public enum AuthenticationStatus
{
    /// <summary>认证通过。</summary>
    Success,

    /// <summary>用户名或密码错误（不区分具体原因，避免账号枚举）。</summary>
    InvalidCredentials,

    /// <summary>当前平台不具备认证能力（非 Linux 或缺少 PAM 运行库）。</summary>
    PlatformUnavailable,

    /// <summary>认证服务内部错误。</summary>
    Error,
}

/// <summary>认证结果，携带 PAM 状态码便于日志排查（不包含密码等敏感信息）。</summary>
public readonly record struct AuthenticationResult(
    AuthenticationStatus Status,
    int PamStatus,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status == AuthenticationStatus.Success;
}
