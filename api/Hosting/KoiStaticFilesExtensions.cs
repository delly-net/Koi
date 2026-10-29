using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Koi.Api.Hosting;

/// <summary>
/// 托管前端 SPA 产物：<c>wwwroot</c> 静态文件 + history 路由回退。
/// 未找到 <c>index.html</c> 时仅记录告警并继续提供 API，不阻断启动。
/// </summary>
public static class KoiStaticFilesExtensions
{
    private const string LoggerCategory = "Koi.StaticFiles";
    private const string SpaIndexFile = "index.html";

    /// <summary>
    /// 注册静态文件中间件。必须安排在业务中间件（同源校验、会话认证等）之前，
    /// 让静态资源请求提前短路，避免每次都查会话库。
    /// </summary>
    public static WebApplication UseKoiStaticFiles(this WebApplication app)
    {
        var logger = CreateLogger(app);
        var rootPath = ResolveSpaRoot(app, logger);
        if (rootPath is null)
        {
            return app;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(rootPath),
            OnPrepareResponse = ApplyCacheHeaders,
        });
        logger.LogInformation("前端静态文件目录：{RootPath}", rootPath);

        return app;
    }

    /// <summary>
    /// 注册兜底端点：<c>/api</c> 下未匹配的路径返回 404 ProblemDetails，其余路径回退 SPA。
    /// 需在业务端点映射之后调用。
    /// </summary>
    public static WebApplication MapKoiFallbacks(this WebApplication app)
    {
        // 接口路径不得回退到前端，否则拼错的 API 会拿到 200 HTML，干扰前端错误归一化。
        app.Map("/api/{**path}", () => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "接口不存在",
            detail: "请求的 API 路径不存在。"));

        // 根目录缺失时 UseKoiStaticFiles 已记录告警，此处不再重复输出。
        var rootPath = ResolveSpaRoot(app, logger: null);
        if (rootPath is null)
        {
            return app;
        }

        var indexPath = Path.Combine(rootPath, SpaIndexFile);

        app.MapFallback(async (HttpContext context) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // 带扩展名的请求视为静态资源，缺失时返回 404，避免把 index.html 当作脚本或样式返回。
            if (Path.HasExtension(context.Request.Path.Value))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await context.Response.SendFileAsync(indexPath);
        });

        return app;
    }

    private static void ApplyCacheHeaders(StaticFileResponseContext context)
    {
        var path = context.Context.Request.Path.Value ?? string.Empty;

        // 入口文件与运行时配置必须每次回源，否则发版或改配置后浏览器仍用旧值。
        if (path.EndsWith('/') ||
            path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/conf/setting.json", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return;
        }

        // Vite 产物文件名带内容指纹，可以长期缓存。
        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }

    private static string? ResolveSpaRoot(WebApplication app, ILogger? logger)
    {
        var options = app.Services.GetRequiredService<IOptions<KoiStaticFilesOptions>>().Value;
        if (!options.Enabled)
        {
            logger?.LogInformation("Koi:StaticFiles:Enabled=false，跳过前端静态文件托管。");
            return null;
        }

        var configured = string.IsNullOrWhiteSpace(options.RootPath) ? "wwwroot" : options.RootPath.Trim();
        var rootPath = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, configured));

        var indexPath = Path.Combine(rootPath, SpaIndexFile);
        if (File.Exists(indexPath))
        {
            return rootPath;
        }

        logger?.LogWarning("未找到前端入口 {IndexPath}，本次仅提供 API 服务。", indexPath);
        return null;
    }

    private static ILogger CreateLogger(WebApplication app)
        => app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
}
