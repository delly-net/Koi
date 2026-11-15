using Koi.Api.Auth;
using Koi.Api.Terminal;

namespace Koi.Api.Editor;

/// <summary>文本编辑端点：文本关联规则只读接口与 root 专用设置接口。</summary>
public static class EditorEndpoints
{
    public static IEndpointRouteBuilder MapEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/editor")
            .WithTags("Editor")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/associations", GetAssociations);
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);

        return app;
    }

    /// <summary>
    /// 读取文本关联规则；所有登录用户可读，且不检查平台或启用状态，
    /// 保证文件管理在非 Linux 构建或配置异常时仍能拿到兜底规则。
    /// </summary>
    private static IResult GetAssociations(EditorSettingsService settingsService)
    {
        return Results.Ok(new EditorAssociationsResponse(settingsService.Current.TextExtensions));
    }

    /// <summary>
    /// 读取文本编辑配置；仅 root 可读，且不检查平台或启用状态，便于管理员修复配置。
    /// </summary>
    private static IResult GetSettings(HttpContext context, EditorSettingsService settingsService)
    {
        var error = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    /// <summary>保存文本编辑配置；保存后立即替换运行时快照，无需重启服务。</summary>
    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        EditorOptions? request,
        EditorSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var error = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的文本编辑配置。");
        }

        var settings = EditorSettingsValidator.Normalize(request);
        if (EditorSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        var userName = context.User.Identity?.Name ?? string.Empty;
        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>
    /// 设置端点仅允许 uid 0 的登录账号。root 判定必须经 <c>getpwnam_r</c> 解析，
    /// 不能按用户名比较；这里刻意跳过平台检查，保证配置损坏时可修复。
    /// </summary>
    private static IResult? AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;
        if (string.IsNullOrEmpty(userName))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再读取文本编辑设置。");
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改文本编辑设置",
                detail: "只有 root（uid 0）账号可以读取或修改文本编辑设置。");
        }

        return null;
    }

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: title,
            detail: detail);
}

/// <summary>文本关联规则响应（任意登录用户可读）。</summary>
public sealed record EditorAssociationsResponse(string[] TextExtensions);
