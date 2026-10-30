using System.Buffers.Binary;
using System.Text.Json;

namespace Koi.Api.Files;

/// <summary>文件辅助进程协议中的错误码，统一映射为 HTTP 状态。</summary>
public static class FileErrorCodes
{
    public const string InvalidPath = "invalid_path";
    public const string NotFound = "not_found";
    public const string AlreadyExists = "already_exists";
    public const string PermissionDenied = "permission_denied";
    public const string IsDirectory = "is_directory";
    public const string NotDirectory = "not_directory";
    public const string NotEmpty = "not_empty";
    public const string TooLarge = "too_large";
    public const string IoError = "io_error";
    public const string HelperFailed = "helper_failed";
    public const string Timeout = "timeout";
}

/// <summary>主进程 → 辅助进程的单次操作请求。</summary>
public sealed record FileHelperRequest
{
    public string Op { get; init; } = string.Empty;

    public uint Uid { get; init; }

    public uint Gid { get; init; }

    public string UserName { get; init; } = string.Empty;

    /// <summary>目标路径；列表/上传时为目标目录。</summary>
    public string? Path { get; init; }

    /// <summary>新建/重命名/上传时的新名称。</summary>
    public string? NewName { get; init; }

    /// <summary>复制/移动/删除的源路径。</summary>
    public string[]? Sources { get; init; }

    /// <summary>复制/移动的目标目录。</summary>
    public string? Target { get; init; }

    public bool Recursive { get; init; }

    public bool Overwrite { get; init; }

    public int MaxEntries { get; init; }

    public long MaxBytes { get; init; }

    /// <summary>允许访问的根目录（辅助进程二次校验，纵深防御）。</summary>
    public string[]? Roots { get; init; }
}

/// <summary>目录条目的传输结构。</summary>
public sealed record FileEntryInfo(
    string Name,
    string Path,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    long ModifiedAt,
    string Mode);

/// <summary>目录列表负载。</summary>
public sealed record FileListPayload(
    string Path,
    string? Parent,
    IReadOnlyList<FileEntryInfo> Entries,
    bool Truncated);

/// <summary>辅助进程 → 主进程的响应头（下载操作后紧跟原始文件字节）。</summary>
public sealed record FileHelperResponse
{
    public bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public FileListPayload? List { get; init; }

    public long? Size { get; init; }

    public string? Name { get; init; }

    public static FileHelperResponse Success() => new() { Ok = true };

    public static FileHelperResponse Failure(string code, string message) =>
        new() { Ok = false, Code = code, Message = message };
}

/// <summary>
/// 辅助进程通信协议：4 字节小端长度 + UTF-8 JSON 头。
/// 上传时请求头之后紧跟原始文件字节；下载时响应头之后紧跟原始文件字节。
/// </summary>
public static class FileProtocol
{
    /// <summary>头部最大字节数，防止恶意超长 JSON 撑爆内存。</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteRequestAsync(
        Stream stream,
        FileHelperRequest request,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<FileHelperRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var json = await ReadFramedJsonAsync(stream, cancellationToken);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileHelperRequest>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task WriteResponseHeaderAsync(
        Stream stream,
        FileHelperResponse response,
        CancellationToken cancellationToken) =>
        WriteFramedJsonAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, Json), cancellationToken);

    public static async Task<FileHelperResponse?> ReadResponseHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var json = await ReadFramedJsonAsync(stream, cancellationToken);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileHelperResponse>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>尽可能填满缓冲区，流提前结束时返回 <c>false</c>。</summary>
    public static async Task<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task WriteFramedJsonAsync(
        Stream stream,
        byte[] json,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]?> ReadFramedJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await TryReadExactlyAsync(stream, header, cancellationToken))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxHeaderBytes)
        {
            return null;
        }

        var json = new byte[length];
        return await TryReadExactlyAsync(stream, json, cancellationToken) ? json : null;
    }
}

/// <summary>文件操作失败（携带协议错误码）。</summary>
public sealed class FileOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
