using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string SpaSettingFile = "conf/setting.json";
    private const string SpaTitleProperty = "title";

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

        ApplyConfiguredTitle(app, rootPath, logger);

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

    /// <summary>
    /// 用 <c>Koi:StaticFiles:Title</c> 覆盖前端运行时配置 <c>conf/setting.json</c> 的
    /// <c>title</c> 属性。仅在静态托管生效时调用；文件缺失、JSON 非法或写入失败均只告警，
    /// 不阻断启动并继续使用原文件。空标题表示跳过覆盖。
    /// </summary>
    private static void ApplyConfiguredTitle(WebApplication app, string rootPath, ILogger logger)
    {
        var options = app.Services.GetRequiredService<IOptions<KoiStaticFilesOptions>>().Value;
        var title = (options.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            logger.LogDebug("Koi:StaticFiles:Title 为空，跳过前端页面标题覆盖。");
            return;
        }

        var settingPath = Path.Combine(rootPath, SpaSettingFile);
        if (!File.Exists(settingPath))
        {
            logger.LogDebug("未找到前端运行时配置 {SettingPath}，跳过页面标题覆盖。", settingPath);
            return;
        }

        try
        {
            var config = JsonNode.Parse(File.ReadAllText(settingPath)) as JsonObject;
            if (config is null)
            {
                logger.LogWarning("前端运行时配置 {SettingPath} 不是 JSON 对象，跳过页面标题覆盖。", settingPath);
                return;
            }

            // 已配置为同一字符串时无需重写；缺失或类型不符时补写。
            if (config.TryGetPropertyValue(SpaTitleProperty, out var existingNode) &&
                existingNode is JsonValue existingValue &&
                existingValue.TryGetValue<string>(out var existingTitle) &&
                string.Equals(existingTitle, title, StringComparison.Ordinal))
            {
                return;
            }

            config[SpaTitleProperty] = title;
            var content = config.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                // 中文标题保持可读，不转义为 \uXXXX。
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + Environment.NewLine;

            WriteTextFileAtomically(settingPath, content);
            logger.LogInformation("已用 Koi:StaticFiles:Title 覆盖前端页面标题：{Title}", title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "更新前端运行时配置 {SettingPath} 的 title 失败，继续使用原文件。", settingPath);
        }
    }

    /// <summary>
    /// 同目录临时文件 + 原子替换，保留原 Unix 权限位；失败时清理临时文件并抛出。
    /// </summary>
    private static void WriteTextFileAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"无法确定配置目录：{path}");
        var tempPath = Path.Combine(directory, $".koi-setting-{Guid.NewGuid():N}.tmp");

        UnixFileMode? mode = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                mode = File.GetUnixFileMode(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                mode = null;
            }
        }

        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (mode is not null && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(tempPath, mode.Value);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // 清理失败不掩盖原始异常。
            }

            throw;
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
