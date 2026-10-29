using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Koi.Api.Auth;

/// <summary>
/// 从会话 Cookie 恢复登录身份，写入 <see cref="HttpContext.User"/> 与会话对象。
/// 后续 WebSocket 终端端点也应复用同一会话校验。
/// </summary>
public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    public const string SessionItemKey = "Koi.Session";

    public async Task InvokeAsync(
        HttpContext context,
        SessionService sessions,
        IOptions<AuthOptions> options)
    {
        var cookieName = options.Value.CookieName;
        if (context.Request.Cookies.TryGetValue(cookieName, out var token) &&
            !string.IsNullOrWhiteSpace(token))
        {
            var session = await sessions.FindAsync(token, context.RequestAborted);
            if (session is not null)
            {
                var identity = new ClaimsIdentity("KoiSession");
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, session.UserName));
                identity.AddClaim(new Claim(ClaimTypes.Name, session.UserName));

                context.User = new ClaimsPrincipal(identity);
                context.Items[SessionItemKey] = session;
            }
        }

        await next(context);
    }
}
