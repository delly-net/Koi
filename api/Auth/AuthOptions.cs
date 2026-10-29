namespace Koi.Api.Auth;

/// <summary>认证相关配置，对应 appsettings.json 中的 Koi:Auth 节。</summary>
public class AuthOptions
{
    public const string SectionName = "Koi:Auth";

    /// <summary>PAM 服务名，决定使用 /etc/pam.d 下的哪个配置。</summary>
    public string PamService { get; set; } = "login";

    /// <summary>会话有效期（小时）。</summary>
    public int SessionExpireHours { get; set; } = 8;

    /// <summary>会话 Cookie 名称。</summary>
    public string CookieName { get; set; } = "koi_session";

    /// <summary>登录接口每个 IP 每分钟允许的尝试次数。</summary>
    public int LoginRateLimitPerMinute { get; set; } = 10;

    /// <summary>额外允许的跨域来源（开发期 Vite 等），同源请求无需配置。</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
