using Koi.Api.Auth;
using Koi.Api.Terminal;

namespace Koi.Api.Nginx;

/// <summary>保存配置文件的请求体。</summary>
public sealed record NginxConfigSaveRequest(string? Content, long ExpectedModifiedAt, bool HasBom);

/// <summary>
/// Nginx 管理端点：conf.d 文件清单、在线读取/保存、配置检测与热加载。
/// </summary>
/// <remarks>
/// 所有配置读写都在服务进程内完成（Nginx 配置通常由 root 所有），授权由
/// <see cref="NginxAccessGuard"/> 的 Enabled → Linux → nginx 可用 → 登录 → 账号 → root/允许组
/// 顺序保证。检测与热加载只执行固定的 <c>nginx -t</c> / <c>nginx -s reload</c>，用户输入不会
/// 进入命令行。
/// </remarks>
public static class NginxEndpoints
{
    public static IEndpointRouteBuilder MapNginxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nginx")
            .WithTags("Nginx")
            .AddEndpointFilter<RequireSessionFilter>();

        // 设置端点只允许 root，且刻意跳过 Enabled 与 nginx 可用性检查，便于修复被关闭或配错的模块。
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);

        group.MapGet("/status", GetStatusAsync);
        group.MapGet("/configs", ListConfigsAsync);
        group.MapGet("/configs/{name}", GetConfigAsync);
        group.MapPut("/configs/{name}", SaveConfigAsync);
        group.MapPost("/test", TestAsync);
        group.MapPost("/reload", ReloadAsync);

        return app;
    }

    private static IResult GetSettings(
        HttpContext context,
        NginxSettingsService settingsService)
    {
        var (error, _) = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        NginxOptions? request,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, userName) = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的 Nginx 管理配置。");
        }

        var settings = NginxSettingsValidator.Normalize(request);
        if (NginxSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>模块状态：启用开关、nginx 版本与当前 conf.d 目录。</summary>
    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        NginxCommandRunner runner,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var version = string.Empty;
        try
        {
            var result = await runner.VersionAsync(CommandTimeout(settings), cancellationToken);
            if (result.Ok)
            {
                version = result.CombinedOutput;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 版本读取失败不影响状态端点，界面只是不显示版本号。
            version = string.Empty;
        }

        return Results.Ok(new
        {
            enabled = settings.Enabled,
            available = true,
            version,
            confDirectory = settings.ConfDirectory,
        });
    }

    /// <summary>conf.d 目录顶层的 *.conf 文件清单。</summary>
    private static async Task<IResult> ListConfigsAsync(
        HttpContext context,
        NginxConfigService configService,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return Results.Ok(configService.List(settings));
        }
        catch (NginxConfigException ex)
        {
            return ToProblem(ex);
        }
    }

    /// <summary>读取一个配置文件的内容。</summary>
    private static async Task<IResult> GetConfigAsync(
        HttpContext context,
        string name,
        NginxConfigService configService,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return Results.Ok(await configService.ReadAsync(settings, name, cancellationToken));
        }
        catch (NginxConfigException ex)
        {
            return ToProblem(ex);
        }
    }

    /// <summary>保存配置文件；expectedModifiedAt 与当前 mtime 不一致时返回 409。</summary>
    private static async Task<IResult> SaveConfigAsync(
        HttpContext context,
        string name,
        NginxConfigSaveRequest? request,
        NginxConfigService configService,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (request?.Content is null)
        {
            return BadRequest("请求内容为空", "请提供要保存的配置内容。");
        }

        try
        {
            var saved = await configService.SaveAsync(
                settings,
                name,
                request.Content,
                request.ExpectedModifiedAt,
                request.HasBom,
                cancellationToken);
            return Results.Ok(new
            {
                ok = true,
                saved.Name,
                saved.Path,
                saved.ModifiedAt,
                saved.Size,
            });
        }
        catch (NginxConfigException ex)
        {
            return ToProblem(ex);
        }
    }

    /// <summary>执行 <c>nginx -t</c>；命令返回非零时仍以 200 返回 success=false 与输出。</summary>
    private static async Task<IResult> TestAsync(
        HttpContext context,
        NginxCommandRunner runner,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var result = await runner.TestAsync(CommandTimeout(settings), MaxOutput(settings), cancellationToken);
        return ToCommandResult(result, "检测 Nginx 配置");
    }

    /// <summary>执行 <c>nginx -s reload</c> 热加载配置。</summary>
    private static async Task<IResult> ReloadAsync(
        HttpContext context,
        NginxCommandRunner runner,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var (error, settings) = await GuardAsync(context, settingsService, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var result = await runner.ReloadAsync(CommandTimeout(settings), MaxOutput(settings), cancellationToken);
        return ToCommandResult(result, "热加载 Nginx 配置");
    }

    /// <summary>公共前置校验：返回错误响应或当前配置快照。</summary>
    private static async Task<(IResult? Error, NginxOptions Settings)> GuardAsync(
        HttpContext context,
        NginxSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        // 每次请求读取最新快照，保证界面保存后立即生效。
        var settings = settingsService.Current;
        var (error, _) = await NginxAccessGuard.AuthorizeAsync(context, settings, cancellationToken);
        return (error, settings);
    }

    /// <summary>设置端点鉴权：仅 uid 0，跳过 Enabled 与 nginx 可用性检查。</summary>
    private static (IResult? Error, string UserName) AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Nginx 管理不可用",
                detail: "Nginx 管理依赖 Linux，当前平台不支持。"), userName);
        }

        if (string.IsNullOrEmpty(userName))
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问 Nginx 管理设置。"), userName);
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return (Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改 Nginx 管理设置",
                detail: "只有 root 账号可以修改 Nginx 管理配置。"), userName);
        }

        return (null, userName);
    }

    private static TimeSpan CommandTimeout(NginxOptions settings) =>
        TimeSpan.FromSeconds(settings.CommandTimeoutSeconds > 0 ? settings.CommandTimeoutSeconds : 30);

    private static long MaxOutput(NginxOptions settings) =>
        settings.MaxOutputBytes > 0 ? settings.MaxOutputBytes : 1024 * 1024;

    private static IResult ToCommandResult(NginxCommandResult result, string title)
    {
        if (result.TimedOut)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: $"{title}超时",
                detail: "nginx 命令执行时间超过上限，已终止。可稍后重试，并检查 nginx 主进程是否卡住。");
        }

        if (result.OutputTooLarge)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"{title}输出过大",
                detail: "nginx 命令输出超过上限，已终止。请检查配置中是否存在大量重复错误。");
        }

        if (result.ExitCode == -1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: $"{title}失败",
                detail: result.CombinedOutput);
        }

        return Results.Ok(new
        {
            success = result.ExitCode == 0,
            output = result.CombinedOutput,
        });
    }

    private static IResult ToProblem(NginxConfigException exception) =>
        Results.Problem(
            statusCode: exception.StatusCode,
            title: exception.Title,
            detail: exception.Message);

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);
}
