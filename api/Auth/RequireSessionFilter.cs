namespace Koi.Api.Auth;

/// <summary>受保护端点的统一未登录响应。</summary>
public sealed class RequireSessionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问该资源。");
        }

        return await next(context);
    }
}
