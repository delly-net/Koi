using Koi.Api.Auth;
using Koi.Api.Terminal;

namespace Koi.Api.Editor;

/// <summary>
/// 文本编辑端点：后缀规则只读接口与 root 专用设置接口。
/// </summary>
/// <remarks>
/// 后缀规则同时决定「文件管理哪些文件按文本打开」与「编辑器使用哪种着色方案」，
/// 数据来自 SQLite 独立表 <c>EditorFileTypes</c>（见 <see cref="EditorFileTypeService"/>）。
/// </remarks>
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
    /// 读取后缀规则；所有登录用户可读，且不检查平台或启用状态，
    /// 保证文件管理在非 Linux 构建或配置异常时仍能拿到兜底规则。
    /// </summary>
    private static IResult GetAssociations(EditorFileTypeService fileTypes)
    {
        return Results.Ok(ToResponse(fileTypes.Current));
    }

    /// <summary>
    /// 读取文本编辑设置；仅 root 可读，且不检查平台或启用状态，便于管理员修复配置。
    /// </summary>
    private static IResult GetSettings(HttpContext context, EditorFileTypeService fileTypes)
    {
        var error = AuthorizeSettings(context);
        return error ?? Results.Ok(ToResponse(fileTypes.Current));
    }

    /// <summary>保存后缀规则；保存后立即替换运行时快照，无需重启服务。</summary>
    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        EditorFileTypesRequest? request,
        EditorFileTypeService fileTypes,
        CancellationToken cancellationToken)
    {
        var error = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request?.FileTypes is null)
        {
            return BadRequest("请求内容为空", "请提供 fileTypes 列表（可以为空数组）。");
        }

        var items = EditorFileTypeValidator.Normalize(request.FileTypes
            .Select(item => new EditorFileTypeInfo(item?.Extension ?? string.Empty, item?.Scheme ?? string.Empty)));
        if (EditorFileTypeValidator.Validate(items) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        var userName = context.User.Identity?.Name ?? string.Empty;
        var saved = await fileTypes.SaveAsync(items, userName, cancellationToken);
        return Results.Ok(ToResponse(saved));
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

    private static EditorFileTypesResponse ToResponse(IReadOnlyList<EditorFileTypeInfo> items) =>
        new(items);

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: title,
            detail: detail);
}

/// <summary>请求中的单条后缀规则；允许空值以便服务端统一校验并返回中文错误。</summary>
public sealed record EditorFileTypeItem(string? Extension, string? Scheme);

/// <summary>后缀规则请求体。</summary>
public sealed record EditorFileTypesRequest(List<EditorFileTypeItem?>? FileTypes);

/// <summary>后缀规则响应（登录用户可读的关联接口与 root 设置接口共用）。</summary>
public sealed record EditorFileTypesResponse(IReadOnlyList<EditorFileTypeInfo> FileTypes);
