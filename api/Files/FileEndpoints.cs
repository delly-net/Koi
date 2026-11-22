using System.Text;
using System.Text.Json;
using Koi.Api.Auth;
using Koi.Api.Terminal;
using Microsoft.AspNetCore.Http.Features;

namespace Koi.Api.Files;

/// <summary>文件管理端点：目录浏览、新建、重命名、删除、复制、移动、上传、下载、在线文本读写。</summary>
public static class FileEndpoints
{
    /// <summary>严格 UTF-8 解码器：遇到非法字节序列抛异常，避免把二进制内容当文本返回。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// 单次递归图片扫描的条目上限。完整路径与相对路径各可达 4096 字符，
    /// 2000 条即可保证响应 JSON 帧（上限 32 MiB）不会被撑爆。
    /// </summary>
    private const int MaxImageEntriesPerScan = 2000;

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files")
            .WithTags("Files")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/roots", GetRoots);
        group.MapGet("/settings", GetSettings);
        group.MapPut("/settings", SaveSettingsAsync);
        // 快速访问目录：按登录账号隔离，任何账号都可维护自己的一份列表。
        group.MapGet("/quick-access", ListQuickAccessAsync);
        group.MapPost("/quick-access", AddQuickAccessAsync);
        group.MapDelete("/quick-access/{id:long}", RemoveQuickAccessAsync);
        group.MapGet("/list", ListAsync);
        // 目录列表 SSE 流：辅助进程分批写出条目，前端边收边渲染，避免大目录一次性响应造成卡顿。
        group.MapGet("/list/stream", StreamListAsync);
        // 图片浏览：递归扫描、原图输出与磁盘缓存缩略图（均沿用文件授权与降权辅助进程）。
        group.MapGet("/images", ListImagesAsync);
        group.MapGet("/image", ServeImageAsync);
        group.MapGet("/thumbnail", GetThumbnailAsync);
        group.MapPost("/mkdir", MakeDirectoryAsync);
        group.MapPost("/touch", CreateFileAsync);
        group.MapPost("/rename", RenameAsync);
        group.MapPost("/delete", DeleteAsync);
        group.MapPost("/copy", (HttpContext context, FileTransferRequest? request,
                FileOperationService service, FileSettingsService settingsService,
                FileTransferProgressRegistry progressRegistry, CancellationToken cancellationToken) =>
            TransferAsync(context, request, service, settingsService, progressRegistry, move: false, cancellationToken));
        group.MapPost("/move", (HttpContext context, FileTransferRequest? request,
                FileOperationService service, FileSettingsService settingsService,
                FileTransferProgressRegistry progressRegistry, CancellationToken cancellationToken) =>
            TransferAsync(context, request, service, settingsService, progressRegistry, move: true, cancellationToken));
        // 复制/移动进度与取消：operationId 由客户端生成，只允许发起账号访问。
        group.MapGet("/transfer-progress/{operationId}", GetTransferProgress);
        group.MapPost("/transfer-progress/{operationId}/cancel", CancelTransfer);
        group.MapPost("/upload", UploadAsync);
        group.MapGet("/download", DownloadAsync);
        group.MapGet("/text", ReadTextAsync);
        group.MapPost("/text", SaveTextAsync);

        return app;
    }

    private static IResult GetRoots(
        HttpContext context,
        FileSettingsService settingsService)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        return Results.Ok(new FileRootsResponse(identity!.HomeDirectory, options.AllowedRoots));
    }

    /// <summary>
    /// 读取文件管理配置；仅 root 可读，且不检查 Enabled 与平台，便于管理员修复被关闭的模块。
    /// </summary>
    private static IResult GetSettings(HttpContext context, FileSettingsService settingsService)
    {
        var error = AuthorizeSettings(context);
        return error ?? Results.Ok(settingsService.Current);
    }

    /// <summary>保存文件管理配置；保存后立即替换运行时快照，无需重启服务。</summary>
    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        FileOptions? request,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var error = AuthorizeSettings(context);
        if (error is not null)
        {
            return error;
        }

        if (request is null)
        {
            return BadRequest("请求内容为空", "请提供完整的文件管理配置。");
        }

        var settings = FileSettingsValidator.Normalize(request);
        if (FileSettingsValidator.Validate(settings) is { } detail)
        {
            return BadRequest("配置不合法", detail);
        }

        var userName = context.User.Identity?.Name ?? string.Empty;
        return Results.Ok(await settingsService.SaveAsync(settings, userName, cancellationToken));
    }

    /// <summary>
    /// 设置端点仅允许 uid 0 的登录账号。root 判定必须经 <c>getpwnam_r</c> 解析，
    /// 不能按用户名比较；这里刻意跳过 Enabled 与平台检查，保证配置损坏时可修复。
    /// </summary>
    private static IResult? AuthorizeSettings(HttpContext context)
    {
        var userName = context.User.Identity?.Name ?? string.Empty;
        if (string.IsNullOrEmpty(userName))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再读取文件管理设置。");
        }

        if (UserProcessIdentityResolver.TryResolve(userName, configuredShell: null) is not { Uid: 0 })
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无权修改文件管理设置",
                detail: "只有 root（uid 0）账号可以读取或修改文件管理设置。");
        }

        return null;
    }

    /// <summary>列出当前账号的快速访问目录。</summary>
    private static async Task<IResult> ListQuickAccessAsync(
        HttpContext context,
        FileSettingsService settingsService,
        FileQuickAccessService quickAccess,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        var items = await quickAccess.ListAsync(identity!.UserName, cancellationToken);
        return Results.Ok(new FileQuickAccessListResponse(items));
    }

    /// <summary>把目录加入当前账号的快速访问列表；重复添加幂等返回既有条目。</summary>
    private static async Task<IResult> AddQuickAccessAsync(
        HttpContext context,
        FileQuickAccessAddRequest? request,
        FileSettingsService settingsService,
        FileQuickAccessService quickAccess,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(request?.Path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        // 主进程以服务账号运行，Directory.Exists 只证明路径存在，真正的访问边界仍是降权后的用户权限。
        if (!Directory.Exists(normalized))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "目录不存在",
                detail: "目标路径不存在或不是目录。");
        }

        var result = await quickAccess.AddAsync(identity!.UserName, normalized, cancellationToken);
        if (result.LimitReached)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "快速访问已满",
                detail: $"每个账号最多保存 {FileQuickAccessService.MaxEntriesPerUser} 个快速访问目录，请先移除不再需要的条目。");
        }

        return Results.Ok(new FileQuickAccessAddResponse(true, result.Created, result.Item!));
    }

    /// <summary>移除当前账号的一条快速访问记录；条目不存在或不属于当前账号时返回 404。</summary>
    private static async Task<IResult> RemoveQuickAccessAsync(
        HttpContext context,
        long id,
        FileSettingsService settingsService,
        FileQuickAccessService quickAccess,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (!await quickAccess.RemoveAsync(identity!.UserName, id, cancellationToken))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "条目不存在",
                detail: "该快速访问条目不存在，或不属于当前账号。");
        }

        return Results.Ok(new FileMutationResponse(true));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        var target = string.IsNullOrWhiteSpace(path) ? identity!.HomeDirectory : path;
        if (ValidatePath(target, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        var request = NewRequest(identity!, options, "list") with { Path = normalized };
        var response = await service.ExecuteAsync(request, cancellationToken);
        return response.Ok ? Results.Ok(response.List) : ToResult(response);
    }

    /// <summary>
    /// 目录列表 SSE 流。流开始前的错误仍返回 ProblemDetails 状态码；
    /// 流开始后的失败以 <c>error</c> 事件下发（此时 HTTP 状态已经是 200）。
    /// </summary>
    private static async Task StreamListAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            await error.ExecuteAsync(context);
            return;
        }

        var target = string.IsNullOrWhiteSpace(path) ? identity!.HomeDirectory : path;
        if (ValidatePath(target, options, out var normalized) is { } invalid)
        {
            await invalid.ExecuteAsync(context);
            return;
        }

        // 主进程预检只为给出准确的 400/404；真正的访问边界仍是降权后的用户权限。
        if (File.Exists(normalized))
        {
            await BadRequest("目标不是目录", "目标路径是一个文件，不能作为目录列举。").ExecuteAsync(context);
            return;
        }

        if (!Directory.Exists(normalized))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "路径不存在",
                detail: "目标目录不存在。").ExecuteAsync(context);
            return;
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store";
        // 关闭 nginx 等反向代理的响应缓冲，否则事件会被攒到流结束才到达浏览器。
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var aborted = context.RequestAborted;
        using var gate = new SemaphoreSlim(1, 1);

        // 心跳与数据帧共用同一把锁，避免并发写入产生交错的 SSE 帧。
        async Task WriteRawAsync(string text, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await gate.WaitAsync(token);
            try
            {
                await response.Body.WriteAsync(bytes, token);
                await response.Body.FlushAsync(token);
            }
            finally
            {
                gate.Release();
            }
        }

        Task WriteEventAsync(string name, object payload, CancellationToken token) =>
            WriteRawAsync(
                $"event: {name}\ndata: {JsonSerializer.Serialize(payload, FileProtocol.Json)}\n\n",
                token);

        var parent = Path.GetDirectoryName(normalized);
        await WriteEventAsync("start", new { path = normalized, parent }, aborted);

        // 辅助进程枚举超大目录期间可能长时间没有数据，定期发送注释行保活。
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
                    await WriteRawAsync(": ping\n\n", heartbeatCts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // 流结束或客户端断开，心跳自然停止。
            }
        });

        FileHelperResponse? result = null;
        var total = 0;
        try
        {
            var request = NewRequest(identity!, options, "list") with
            {
                Path = normalized,
                Stream = true,
            };

            result = await service.ExecuteStreamingAsync(
                request,
                async chunk =>
                {
                    total += chunk.Entries.Count;
                    await WriteEventAsync("entries", new { entries = chunk.Entries }, aborted);
                },
                aborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 客户端断开或写入失败：辅助进程已由 RunAsync 的 finally 终止，安静结束。
            return;
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }

        try
        {
            if (result.Ok)
            {
                await WriteEventAsync(
                    "done",
                    new
                    {
                        path = result.List?.Path ?? normalized,
                        parent = result.List?.Parent ?? parent,
                        truncated = result.List?.Truncated ?? false,
                        total,
                    },
                    aborted);
                return;
            }

            await WriteEventAsync(
                "error",
                new
                {
                    code = result.Code,
                    status = StatusFor(result.Code),
                    title = TitleFor(result.Code),
                    detail = result.Message,
                },
                aborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 客户端已断开，错误无法再下发。
        }
    }

    /// <summary>递归加载目录（含子目录）下的所有图片；数量受 MaxListEntries 限制。</summary>
    private static async Task<IResult> ListImagesAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        var target = string.IsNullOrWhiteSpace(path) ? identity!.HomeDirectory : path;
        if (ValidatePath(target, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        var request = NewRequest(identity!, options, "imagelist") with
        {
            Path = normalized,
            MaxEntries = Math.Clamp(
                Math.Min(options.MaxListEntries, MaxImageEntriesPerScan),
                1,
                MaxImageEntriesPerScan),
        };
        var response = await service.ExecuteAsync(request, cancellationToken);
        if (!response.Ok)
        {
            return ToResult(response);
        }

        if (response.Images is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "加载失败",
                detail: "文件辅助进程没有返回图片列表。");
        }

        return Results.Ok(response.Images);
    }

    /// <summary>
    /// 输出图片原图：复用下载流，但设置图片 MIME 与 inline，便于浏览器/查看器直接显示。
    /// <c>Content-Security-Policy: sandbox</c> 防止同源直接打开 SVG 时执行脚本。
    /// </summary>
    private static async Task ServeImageAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            await error.ExecuteAsync(context);
            return;
        }

        if (ValidatePath(path, options, out var normalized) is { } invalid)
        {
            await invalid.ExecuteAsync(context);
            return;
        }

        var contentType = ImageFileTypes.GetContentType(normalized);
        if (contentType is null)
        {
            await BadRequest("不是图片", "只允许预览受支持的图片文件。").ExecuteAsync(context);
            return;
        }

        var request = NewRequest(identity!, options, "download") with { Path = normalized };
        var response = await service.DownloadAsync(
            request,
            header =>
            {
                context.Response.ContentType = contentType;
                context.Response.ContentLength = header.Size ?? 0;
                context.Response.Headers.ContentDisposition = "inline";
                context.Response.Headers.CacheControl = "private, max-age=86400";
                // 同一浏览器切换账号时不得复用上一个会话缓存的图片。
                context.Response.Headers["Vary"] = "Cookie";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                return Task.CompletedTask;
            },
            context.Response.Body,
            cancellationToken);

        if (!response.Ok && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await ToResult(response).ExecuteAsync(context);
        }
    }

    /// <summary>
    /// 输出 128×128 以内的缓存缩略图。首次请求由降权辅助进程生成并落盘，
    /// 之后直接读取缓存文件，不重复解码原图。
    /// </summary>
    private static async Task GetThumbnailAsync(
        HttpContext context,
        string? path,
        string? v,
        FileOperationService service,
        FileSettingsService settingsService,
        ImageThumbnailService thumbnails,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            await error.ExecuteAsync(context);
            return;
        }

        if (ValidatePath(path, options, out var normalized) is { } invalid)
        {
            await invalid.ExecuteAsync(context);
            return;
        }

        if (ImageFileTypes.GetContentType(normalized) is null)
        {
            await BadRequest("不是图片", "只允许为受支持的图片文件生成缩略图。").ExecuteAsync(context);
            return;
        }

        if (!ImageFileTypes.CanThumbnail(normalized))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "无法生成缩略图",
                detail: "该图片格式不支持生成缩略图，可点击后查看原图。").ExecuteAsync(context);
            return;
        }

        // 缓存按登录账号隔离：命中缓存也不能绕过该账号自身的文件读取权限。
        var cachePath = thumbnails.GetCachePath(normalized, v, identity!.UserName);
        if (!thumbnails.TryGetCached(cachePath))
        {
            var request = NewRequest(identity!, options, "thumbnail") with { Path = normalized };
            var created = await thumbnails.CreateAsync(request, cachePath, cancellationToken);
            if (!created.Ok)
            {
                await ToResult(created).ExecuteAsync(context);
                return;
            }
        }

        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        // 同一浏览器切换账号时不得复用上一个会话缓存的缩略图。
        context.Response.Headers["Vary"] = "Cookie";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await Results.File(cachePath, "image/png").ExecuteAsync(context);
    }

    private static async Task<IResult> MakeDirectoryAsync(
        HttpContext context,
        FilePathRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(request?.Path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        var response = await service.ExecuteAsync(
            NewRequest(identity!, options, "mkdir") with { Path = normalized },
            cancellationToken);
        return ToResult(response);
    }

    private static async Task<IResult> CreateFileAsync(
        HttpContext context,
        FilePathRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(request?.Path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        var response = await service.ExecuteAsync(
            NewRequest(identity!, options, "touch") with { Path = normalized },
            cancellationToken);
        return ToResult(response);
    }

    private static async Task<IResult> RenameAsync(
        HttpContext context,
        FileRenameRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(request?.Path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        if (!FilePaths.IsValidName(request?.NewName))
        {
            return BadRequest("名称不合法", "新名称不能为空，且不得包含路径分隔符。");
        }

        var response = await service.ExecuteAsync(
            NewRequest(identity!, options, "rename") with { Path = normalized, NewName = request!.NewName },
            cancellationToken);
        return ToResult(response);
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        FileDeleteRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (request?.Paths is not { Length: > 0 })
        {
            return BadRequest("参数不合法", "请指定要删除的路径。");
        }

        var paths = new List<string>(request.Paths.Length);
        foreach (var item in request.Paths)
        {
            if (ValidatePath(item, options, out var normalized) is { } invalid)
            {
                return invalid;
            }

            paths.Add(normalized);
        }

        var response = await service.ExecuteAsync(
            NewRequest(identity!, options, "delete") with
            {
                Sources = paths.ToArray(),
                Recursive = request.Recursive,
            },
            cancellationToken);
        return ToResult(response);
    }

    private static async Task<IResult> TransferAsync(
        HttpContext context,
        FileTransferRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        FileTransferProgressRegistry progressRegistry,
        bool move,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (request?.Sources is not { Length: > 0 })
        {
            return BadRequest("参数不合法", "请指定源路径。");
        }

        if (ValidatePath(request.Target, options, out var target) is { } invalid)
        {
            return invalid;
        }

        var sources = new List<string>(request.Sources.Length);
        foreach (var item in request.Sources)
        {
            if (ValidatePath(item, options, out var normalized) is { } sourceInvalid)
            {
                return sourceInvalid;
            }

            sources.Add(normalized);
        }

        // operationId 可选：不带时保持既有的无进度同步行为。
        FileTransferOperation? operation = null;
        if (!string.IsNullOrEmpty(request.OperationId))
        {
            if (!FileTransferProgressRegistry.IsValidOperationId(request.OperationId))
            {
                return BadRequest("操作标识不合法", "operationId 只能由字母、数字与连字符组成，长度 8–64。");
            }

            if (!progressRegistry.TryBegin(
                    request.OperationId,
                    identity!.UserName,
                    move ? "move" : "copy",
                    out operation))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "操作标识重复",
                    detail: "该粘贴操作已在进行中，请稍后重试。");
            }
        }

        FileHelperResponse response;
        try
        {
            response = await service.ExecuteAsync(
                NewRequest(identity!, options, move ? "move" : "copy") with
                {
                    Sources = sources.ToArray(),
                    Target = target,
                    Overwrite = request.Overwrite,
                    OperationId = operation?.Id,
                },
                operation,
                cancellationToken);
        }
        catch
        {
            // 未预期异常也要收口登记项，避免其长期停留在「进行中」。
            operation?.Complete(FileHelperResponse.Failure(FileErrorCodes.IoError, "文件操作异常终止。"));
            throw;
        }

        // 取消属于用户主动行为：以 200 + {ok:false,cancelled:true} 返回，
        // 不与「目标已存在」的 409 覆盖确认混淆。复制若已在取消前完成，仍按成功返回。
        if (operation is not null)
        {
            operation.Complete(response);
            if (!response.Ok && operation.CancellationRequested)
            {
                return Results.Ok(new FileMutationResponse(false, true));
            }
        }

        return ToResult(response);
    }

    /// <summary>读取复制/移动进度；不属于当前账号或已过期时返回 404。</summary>
    private static IResult GetTransferProgress(
        HttpContext context,
        string operationId,
        FileSettingsService settingsService,
        FileTransferProgressRegistry progressRegistry)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (!FileTransferProgressRegistry.IsValidOperationId(operationId) ||
            !progressRegistry.TryGet(operationId, identity!.UserName, out var operation))
        {
            return TransferNotFound();
        }

        return Results.Ok(operation.Snapshot());
    }

    /// <summary>取消复制/移动：终止辅助进程；已复制的条目保留，不回滚。</summary>
    private static IResult CancelTransfer(
        HttpContext context,
        string operationId,
        FileSettingsService settingsService,
        FileTransferProgressRegistry progressRegistry)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (!FileTransferProgressRegistry.IsValidOperationId(operationId) ||
            !progressRegistry.TryGet(operationId, identity!.UserName, out var operation))
        {
            return TransferNotFound();
        }

        var cancelled = operation.Cancel();
        return Results.Ok(new FileTransferCancelResponse(true, cancelled, operation.IsCompleted));
    }

    private static IResult TransferNotFound() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "进度不存在",
            detail: "该粘贴操作不存在、不属于当前账号或已结束过久。");

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        string? path,
        // 必须用可空类型：Minimal API 会把非可空且无默认值的参数当作必填查询参数，
        // 首次上传（前端不传 overwrite）会在参数绑定阶段直接被拒为 400。
        bool? overwrite,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        var target = string.IsNullOrWhiteSpace(path) ? identity!.HomeDirectory : path;
        if (ValidatePath(target, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        if (!context.Request.HasFormContentType)
        {
            return BadRequest("请求不合法", "上传必须使用 multipart/form-data。");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return BadRequest("请求不合法", "请选择要上传的文件。");
        }

        if (file.Length > options.MaxUploadBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "文件过大",
                detail: $"单个文件不能超过 {options.MaxUploadBytes} 字节。");
        }

        var name = Path.GetFileName(file.FileName);
        if (!FilePaths.IsValidName(name))
        {
            return BadRequest("文件名不合法", "文件名不能为空，且不得包含路径分隔符。");
        }

        await using var content = file.OpenReadStream();
        var response = await service.UploadAsync(
            NewRequest(identity!, options, "upload") with
            {
                Path = normalized,
                NewName = name,
                Overwrite = overwrite ?? false,
            },
            content,
            cancellationToken);

        return ToResult(response);
    }

    private static async Task DownloadAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            await error.ExecuteAsync(context);
            return;
        }

        if (ValidatePath(path, options, out var normalized) is { } invalid)
        {
            await invalid.ExecuteAsync(context);
            return;
        }

        var request = NewRequest(identity!, options, "download") with { Path = normalized };
        var response = await service.DownloadAsync(
            request,
            header =>
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = header.Size ?? 0;
                context.Response.Headers.ContentDisposition = BuildContentDisposition(
                    header.Name ?? Path.GetFileName(normalized));
                return Task.CompletedTask;
            },
            context.Response.Body,
            cancellationToken);

        if (!response.Ok && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await ToResult(response).ExecuteAsync(context);
        }
    }

    /// <summary>读取文本文件内容（UTF-8），大小受 <c>Koi:Files:MaxTextFileBytes</c> 限制。</summary>
    private static async Task<IResult> ReadTextAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        var limit = Math.Max(options.MaxTextFileBytes, 1);
        var request = NewRequest(identity!, options, "readtext") with
        {
            Path = normalized,
            MaxBytes = limit,
        };

        var (response, content) = await service.ReadTextAsync(request, limit, cancellationToken);
        if (!response.Ok)
        {
            return ToResult(response);
        }

        if (content is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "读取失败",
                detail: "文件辅助进程没有返回文本内容。");
        }

        if (!TryDecodeText(content, out var text, out var hasBom))
        {
            return BadRequest("文本编码不支持", "该文件不是有效的 UTF-8 文本，无法在线编辑。");
        }

        if (text.Contains('\0'))
        {
            return BadRequest("文件不是文本", "该文件包含二进制内容，无法在线编辑。");
        }

        return Results.Ok(new FileTextResponse(
            normalized,
            text,
            response.ModifiedAt ?? 0,
            content.LongLength,
            hasBom));
    }

    /// <summary>保存在线编辑的文本；<c>expectedModifiedAt</c> 用于乐观并发冲突检测。</summary>
    private static async Task<IResult> SaveTextAsync(
        HttpContext context,
        FileTextSaveRequest? request,
        FileOperationService service,
        FileSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var options = settingsService.Current;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        if (ValidatePath(request?.Path, options, out var normalized) is { } invalid)
        {
            return invalid;
        }

        if (request?.Content is null)
        {
            return BadRequest("请求不合法", "请提供要保存的文本内容。");
        }

        var limit = Math.Max(options.MaxTextFileBytes, 1);
        var body = Encoding.UTF8.GetBytes(request.Content);
        var total = body.LongLength + (request.HasBom ? Utf8Bom.Length : 0);
        if (total > limit || total > int.MaxValue)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "文件过大",
                detail: $"在线编辑的内容不能超过 {limit} 字节。");
        }

        using var content = new MemoryStream((int)total);
        if (request.HasBom)
        {
            content.Write(Utf8Bom);
        }

        content.Write(body);
        content.Position = 0;

        var helperRequest = NewRequest(identity!, options, "writetext") with
        {
            Path = normalized,
            MaxBytes = limit,
            ExpectedModifiedAt = request.ExpectedModifiedAt,
        };

        var response = await service.SaveTextAsync(helperRequest, content, cancellationToken);
        if (!response.Ok)
        {
            return ToResult(response);
        }

        return Results.Ok(new FileTextSaveResponse(true, response.ModifiedAt ?? 0, response.Size ?? total));
    }

    private static bool TryDecodeText(byte[] data, out string content, out bool hasBom)
    {
        hasBom = data.Length >= Utf8Bom.Length && data.AsSpan(0, Utf8Bom.Length).SequenceEqual(Utf8Bom);
        var offset = hasBom ? Utf8Bom.Length : 0;

        try
        {
            content = StrictUtf8.GetString(data, offset, data.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static FileHelperRequest NewRequest(
        UserProcessIdentity identity,
        FileOptions options,
        string operation) => new()
        {
            Op = operation,
            Uid = identity.Uid,
            Gid = identity.Gid,
            UserName = identity.UserName,
            Roots = options.AllowedRoots,
            MaxEntries = options.MaxListEntries,
            MaxBytes = options.MaxUploadBytes,
        };

    /// <summary>公共前置校验：开关、平台、登录身份与系统账号。</summary>
    private static IResult? Guard(
        HttpContext context,
        FileOptions options,
        out UserProcessIdentity? identity)
    {
        identity = null;

        if (!options.Enabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "文件管理已关闭",
                detail: "管理员已禁用文件管理功能。");
        }

        if (!OperatingSystem.IsLinux())
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "文件管理不可用",
                detail: "文件管理依赖 Linux 文件系统权限模型，当前平台不支持。");
        }

        var userName = context.User.Identity?.Name;
        if (string.IsNullOrEmpty(userName))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "未登录",
                detail: "请先登录后再访问文件管理。");
        }

        identity = UserProcessIdentityResolver.TryResolve(userName, configuredShell: null);
        if (identity is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "无法访问文件",
                detail: "当前账号在系统账号库中不存在，或用户名不合法。");
        }

        return null;
    }

    private static IResult? ValidatePath(string? value, FileOptions options, out string normalized)
    {
        if (!FilePaths.TryNormalize(value, out normalized))
        {
            return BadRequest("路径不合法", "路径必须是绝对路径，且不能包含控制字符。");
        }

        if (!FilePaths.IsWithinRoots(FilePaths.ResolveRealPath(normalized), options.AllowedRoots))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "路径超出允许范围",
                detail: "该路径不在管理员配置的可访问根目录内。");
        }

        return null;
    }

    private static IResult ToResult(FileHelperResponse response)
    {
        if (response.Ok)
        {
            return Results.Ok(new FileMutationResponse(true));
        }

        var status = StatusFor(response.Code);

        return Results.Problem(
            statusCode: status,
            title: TitleFor(response.Code),
            detail: response.Message);
    }

    /// <summary>文件辅助进程错误码到 HTTP 状态的映射（SSE 流内错误事件复用同一口径）。</summary>
    private static int StatusFor(string? code) => code switch
    {
        FileErrorCodes.InvalidPath => StatusCodes.Status400BadRequest,
        FileErrorCodes.NotFound => StatusCodes.Status404NotFound,
        FileErrorCodes.AlreadyExists => StatusCodes.Status409Conflict,
        FileErrorCodes.PermissionDenied => StatusCodes.Status403Forbidden,
        FileErrorCodes.IsDirectory => StatusCodes.Status400BadRequest,
        FileErrorCodes.NotDirectory => StatusCodes.Status400BadRequest,
        FileErrorCodes.NotEmpty => StatusCodes.Status409Conflict,
        FileErrorCodes.Conflict => StatusCodes.Status409Conflict,
        FileErrorCodes.TooLarge => StatusCodes.Status413PayloadTooLarge,
        FileErrorCodes.UnsupportedImage => StatusCodes.Status415UnsupportedMediaType,
        FileErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        // 正常情况下取消由 TransferAsync 转成 200 {ok:false,cancelled:true}；
        // 这里仅作兜底，避免误入 409 覆盖确认链路。
        FileErrorCodes.Cancelled => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(string? code) => code switch
    {
        FileErrorCodes.InvalidPath => "参数不合法",
        FileErrorCodes.NotFound => "路径不存在",
        FileErrorCodes.AlreadyExists => "目标已存在",
        FileErrorCodes.PermissionDenied => "权限不足",
        FileErrorCodes.IsDirectory => "目标是目录",
        FileErrorCodes.NotDirectory => "目标不是目录",
        FileErrorCodes.NotEmpty => "目录非空",
        FileErrorCodes.Conflict => "文件已被修改",
        FileErrorCodes.TooLarge => "文件过大",
        FileErrorCodes.UnsupportedImage => "无法生成缩略图",
        FileErrorCodes.Timeout => "操作超时",
        FileErrorCodes.Cancelled => "操作已取消",
        _ => "文件操作失败",
    };

    private static IResult BadRequest(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    private static string BuildContentDisposition(string fileName)
    {
        var fallback = new string(fileName
            .Select(ch => ch < 32 || ch > 126 || ch is '"' or '\\' ? '_' : ch)
            .ToArray());

        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}

public sealed record FilePathRequest(string? Path);

public sealed record FileRenameRequest(string? Path, string? NewName);

public sealed record FileDeleteRequest(string[]? Paths, bool Recursive);

public sealed record FileTransferRequest(
    string[]? Sources,
    string? Target,
    bool Overwrite,
    string? OperationId = null);

public sealed record FileTextSaveRequest(string? Path, string? Content, long ExpectedModifiedAt, bool HasBom);

public sealed record FileRootsResponse(string Home, string[] Roots);

public sealed record FileQuickAccessAddRequest(string? Path);

public sealed record FileQuickAccessListResponse(FileQuickAccessEntry[] Items);

public sealed record FileQuickAccessAddResponse(bool Ok, bool Created, FileQuickAccessEntry Item);

/// <summary>文件变更结果；<paramref name="Cancelled"/> 仅在用户取消粘贴时为 true（HTTP 200）。</summary>
public sealed record FileMutationResponse(bool Ok, bool Cancelled = false);

public sealed record FileTextResponse(string Path, string Content, long ModifiedAt, long Size, bool HasBom);

public sealed record FileTextSaveResponse(bool Ok, long ModifiedAt, long Size);
