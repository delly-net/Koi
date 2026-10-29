using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Koi.Api.Auth;

/// <summary>
/// 对非安全方法做来源校验，配合 SameSite=Lax 缓解 CSRF。
/// 同源请求，或来源命中配置白名单时放行。
/// </summary>
public sealed class SameOriginMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete,
    };

    public async Task InvokeAsync(HttpContext context, IOptions<AuthOptions> options)
    {
        if (UnsafeMethods.Contains(context.Request.Method) &&
            context.Request.Headers.TryGetValue(HeaderNames.Origin, out var originValues) &&
            !string.IsNullOrEmpty(originValues.ToString()))
        {
            var origin = originValues.ToString();
            if (!IsAllowedOrigin(context, origin, options.Value.AllowedOrigins))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    status = StatusCodes.Status403Forbidden,
                    title = "请求来源不被允许",
                    detail = "检测到跨站请求，已拒绝执行。",
                }, cancellationToken: context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// 判断来源是否被允许：同源或命中配置白名单。
    /// 公开给 WebSocket 终端端点复用（握手是 GET，不经本中间件的非安全方法校验）。
    /// </summary>
    public static bool IsAllowedOrigin(HttpContext context, string origin, string[] allowedOrigins)
    {
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host.Value}";
        if (string.Equals(origin, requestOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowedOrigins.Any(allowed =>
            string.Equals(origin.TrimEnd('/'), allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}
