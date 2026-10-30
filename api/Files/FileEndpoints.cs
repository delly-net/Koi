using System.Text;
using Koi.Api.Auth;
using Koi.Api.Terminal;
using Microsoft.Extensions.Options;

namespace Koi.Api.Files;

/// <summary>文件管理端点：目录浏览、新建、重命名、删除、复制、移动、上传、下载、在线文本读写。</summary>
public static class FileEndpoints
{
    /// <summary>严格 UTF-8 解码器：遇到非法字节序列抛异常，避免把二进制内容当文本返回。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files")
            .WithTags("Files")
            .AddEndpointFilter<RequireSessionFilter>();

        group.MapGet("/roots", GetRoots);
        group.MapGet("/list", ListAsync);
        group.MapPost("/mkdir", MakeDirectoryAsync);
        group.MapPost("/touch", CreateFileAsync);
        group.MapPost("/rename", RenameAsync);
        group.MapPost("/delete", DeleteAsync);
        group.MapPost("/copy", (HttpContext context, FileTransferRequest? request,
                FileOperationService service, IOptions<FileOptions> options, CancellationToken cancellationToken) =>
            TransferAsync(context, request, service, options, move: false, cancellationToken));
        group.MapPost("/move", (HttpContext context, FileTransferRequest? request,
                FileOperationService service, IOptions<FileOptions> options, CancellationToken cancellationToken) =>
            TransferAsync(context, request, service, options, move: true, cancellationToken));
        group.MapPost("/upload", UploadAsync);
        group.MapGet("/download", DownloadAsync);
        group.MapGet("/text", ReadTextAsync);
        group.MapPost("/text", SaveTextAsync);

        return app;
    }

    private static IResult GetRoots(
        HttpContext context,
        IOptions<FileOptions> optionsAccessor)
    {
        var options = optionsAccessor.Value;
        if (Guard(context, options, out var identity) is { } error)
        {
            return error;
        }

        return Results.Ok(new FileRootsResponse(identity!.HomeDirectory, options.AllowedRoots));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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

    private static async Task<IResult> MakeDirectoryAsync(
        HttpContext context,
        FilePathRequest? request,
        FileOperationService service,
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        bool move,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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

        var response = await service.ExecuteAsync(
            NewRequest(identity!, options, move ? "move" : "copy") with
            {
                Sources = sources.ToArray(),
                Target = target,
                Overwrite = request.Overwrite,
            },
            cancellationToken);
        return ToResult(response);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        string? path,
        bool overwrite,
        FileOperationService service,
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
                Overwrite = overwrite,
            },
            content,
            cancellationToken);

        return ToResult(response);
    }

    private static async Task DownloadAsync(
        HttpContext context,
        string? path,
        FileOperationService service,
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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
        IOptions<FileOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
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

        var status = response.Code switch
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
            FileErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Problem(
            statusCode: status,
            title: TitleFor(response.Code),
            detail: response.Message);
    }

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
        FileErrorCodes.Timeout => "操作超时",
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

public sealed record FileTransferRequest(string[]? Sources, string? Target, bool Overwrite);

public sealed record FileTextSaveRequest(string? Path, string? Content, long ExpectedModifiedAt, bool HasBom);

public sealed record FileRootsResponse(string Home, string[] Roots);

public sealed record FileMutationResponse(bool Ok);

public sealed record FileTextResponse(string Path, string Content, long ModifiedAt, long Size, bool HasBom);

public sealed record FileTextSaveResponse(bool Ok, long ModifiedAt, long Size);
