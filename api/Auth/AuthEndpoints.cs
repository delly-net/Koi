using Koi.Api.Data;
using Microsoft.Extensions.Options;

namespace Koi.Api.Auth;

/// <summary>认证相关端点：登录、退出、获取当前用户。</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .RequireRateLimiting(LoginRateLimitPolicy);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", GetCurrentUser)
            .AddEndpointFilter<RequireSessionFilter>();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? request,
        HttpContext context,
        IAuthenticator authenticator,
        SessionService sessions,
        IOptions<AuthOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Koi.Api.Auth.Login");

        if (request is null ||
            string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrEmpty(request.Password))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "参数不合法",
                detail: "用户名和密码不能为空。");
        }

        var userName = request.UserName.Trim();
        if (userName.Length > 64 || userName.Any(char.IsControl))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "参数不合法",
                detail: "用户名格式不正确。");
        }

        var result = authenticator.Authenticate(userName, request.Password);

        switch (result.Status)
        {
            case AuthenticationStatus.PlatformUnavailable:
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "认证服务不可用",
                    detail: "Koi 的系统账号认证依赖 Linux PAM，当前平台不支持。");

            case AuthenticationStatus.Error:
                logger.LogWarning(
                    "PAM 认证发生错误，状态码 {PamStatus}：{Message}",
                    result.PamStatus,
                    result.ErrorMessage);
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "认证失败",
                    detail: "认证服务暂时不可用，请稍后重试。");

            case AuthenticationStatus.InvalidCredentials:
                logger.LogInformation(
                    "用户 {UserName} 登录失败（PAM 状态码 {PamStatus}）。",
                    userName,
                    result.PamStatus);
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "登录失败",
                    detail: "用户名或密码错误。");

            case AuthenticationStatus.Success:
                break;
        }

        // 登录成功：先吊销浏览器中可能残留的旧会话，再签发新会话。
        if (context.Request.Cookies.TryGetValue(options.Value.CookieName, out var oldToken) &&
            !string.IsNullOrWhiteSpace(oldToken))
        {
            await sessions.RevokeAsync(oldToken, cancellationToken);
        }

        var (token, session) = await sessions.CreateAsync(
            userName,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        context.Response.Cookies.Append(
            options.Value.CookieName,
            token,
            BuildCookieOptions(context, session.ExpiresAt));

        logger.LogInformation("用户 {UserName} 登录成功。", userName);
        return Results.Ok(new LoginResponse(session.UserName, session.ExpiresAt));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        SessionService sessions,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var cookieName = options.Value.CookieName;
        if (context.Request.Cookies.TryGetValue(cookieName, out var token) &&
            !string.IsNullOrWhiteSpace(token))
        {
            await sessions.RevokeAsync(token, cancellationToken);
        }

        context.Response.Cookies.Delete(cookieName, BuildCookieOptions(context, null));
        return Results.NoContent();
    }

    private static IResult GetCurrentUser(HttpContext context)
    {
        if (context.Items[SessionAuthenticationMiddleware.SessionItemKey] is not SessionEntity session)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问该资源。");
        }

        return Results.Ok(new LoginResponse(session.UserName, session.ExpiresAt));
    }

    private static CookieOptions BuildCookieOptions(HttpContext context, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };
}

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record LoginResponse(string UserName, DateTimeOffset ExpiresAt);
