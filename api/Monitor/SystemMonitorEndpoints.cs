using Koi.Api.Auth;
using Microsoft.Extensions.Options;

namespace Koi.Api.Monitor;

/// <summary>服务器状态端点：首页展示的 CPU、内存、磁盘与网络实时指标。</summary>
/// <remarks>
/// 指标为本机聚合数据，对全部已登录用户开放；非 Linux（无 /proc）或功能关闭时返回 503。
/// </remarks>
public static class SystemMonitorEndpoints
{
    public static IEndpointRouteBuilder MapSystemMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system")
            .WithTags("System")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/status", GetStatusAsync);

        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        SystemMonitorService monitor,
        IOptions<SystemMonitorOptions> optionsAccessor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!optionsAccessor.Value.Enabled || !OperatingSystem.IsLinux())
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "系统状态不可用",
                detail: "系统状态采集依赖 Linux /proc，当前平台不支持或功能已关闭。");
        }

        try
        {
            var snapshot = await monitor.GetSnapshotAsync(cancellationToken);
            return Results.Ok(snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loggerFactory.CreateLogger("Koi.Api.Monitor").LogWarning(exception, "读取 /proc 系统状态失败。");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "系统状态不可用",
                detail: "无法读取 Linux /proc 系统状态，请确认服务运行在 Linux 主机上。");
        }
    }
}
