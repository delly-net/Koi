using System.Text;
using System.Text.RegularExpressions;

namespace Koi.Api.Nginx;

/// <summary>Nginx 配置文件清单项。</summary>
public sealed record NginxConfigEntry(string Name, long Size, long ModifiedAt);

/// <summary>Nginx 配置文件清单结果。</summary>
public sealed record NginxConfigList(string Directory, NginxConfigEntry[] Files, bool Truncated);

/// <summary>Nginx 配置文件内容。</summary>
public sealed record NginxConfigContent(
    string Name,
    string Path,
    string Content,
    long ModifiedAt,
    long Size,
    bool HasBom);

/// <summary>Nginx 配置文件保存结果。</summary>
public sealed record NginxConfigSaveResult(string Name, string Path, long ModifiedAt, long Size);

/// <summary>Nginx 配置文件操作失败；端点据此返回 ProblemDetails。</summary>
public sealed class NginxConfigException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;
}

/// <summary>
/// conf.d 目录下 <c>*.conf</c> 文件的读取与保存。
/// </summary>
/// <remarks>
/// Nginx 配置通常由 root 所有，文件辅助进程会降权到登录用户而无法写入，因此这里在服务进程内
/// 直接读写。安全边界由以下规则共同保证：只接受合法的 <c>*.conf</c> 文件名（不含路径分隔符、
/// 不以点开头）、只操作配置目录顶层的普通文件、拒绝符号链接、组合路径后再次做目录包含校验、
/// 受配置项大小与条目上限约束。
/// <para>
/// 保存使用同目录临时文件 + 保留原 Unix 权限位 + 原子替换；临时文件不以 <c>.conf</c> 结尾，
/// 即使残留也不会被 nginx 加载。保存本身不执行 nginx 检测，检测是独立的手动动作。
/// </para>
/// </remarks>
public sealed class NginxConfigService(ILogger<NginxConfigService> logger)
{
    /// <summary>
    /// 配置文件名的严格白名单：字母、数字或下划线开头，随后允许字母、数字、<c>. _ -</c>，
    /// 最多 255 个字符并以 <c>.conf</c> 结尾。排除 <c>..</c>、路径分隔符与临时文件。
    /// </summary>
    private static readonly Regex FileNamePattern = new(
        @"^[A-Za-z0-9_][A-Za-z0-9._-]{0,254}\.conf$",
        RegexOptions.Compiled);

    /// <summary>严格 UTF-8：非法字节直接抛异常，而不是替换为 U+FFFD。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>列出配置目录顶层的合法 <c>*.conf</c> 普通文件。</summary>
    public NginxConfigList List(NginxOptions settings)
    {
        var directory = ResolveDirectory(settings);
        var accepted = new List<NginxConfigEntry>();
        var limit = settings.MaxListEntries > 0 ? settings.MaxListEntries : 1000;
        var truncated = false;

        var info = new DirectoryInfo(directory);
        try
        {
            foreach (var file in info.EnumerateFiles())
            {
                if (!FileNamePattern.IsMatch(file.Name) || IsLink(file))
                {
                    continue;
                }

                try
                {
                    var entry = new NginxConfigEntry(
                        file.Name,
                        file.Length,
                        ToUnixMilliseconds(file.LastWriteTimeUtc));
                    accepted.Add(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 清单枚举期间文件被删除或不可读：跳过该条目，不影响其余文件。
                    logger.LogWarning(ex, "读取 Nginx 配置文件信息失败：{Path}", file.FullName);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NginxConfigException(
                StatusCodes.Status403Forbidden,
                "无权读取配置目录",
                $"无法读取 Nginx 配置目录 {directory}：{ex.Message}");
        }
        catch (IOException ex)
        {
            throw new NginxConfigException(
                StatusCodes.Status500InternalServerError,
                "读取配置目录失败",
                $"无法读取 Nginx 配置目录 {directory}：{ex.Message}");
        }

        accepted.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        if (accepted.Count > limit)
        {
            accepted.RemoveRange(limit, accepted.Count - limit);
            truncated = true;
        }

        return new NginxConfigList(directory, [.. accepted], truncated);
    }

    /// <summary>读取一个配置文件；仅支持 UTF-8（可带 BOM）。</summary>
    public async Task<NginxConfigContent> ReadAsync(
        NginxOptions settings,
        string name,
        CancellationToken cancellationToken)
    {
        var path = ResolveFile(settings, name);
        var file = new FileInfo(path);
        if (!file.Exists || IsLink(file))
        {
            throw NotFound(name);
        }

        var maxBytes = MaxFileBytes(settings);
        if (file.Length > maxBytes)
        {
            throw TooLarge();
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NginxConfigException(
                StatusCodes.Status500InternalServerError,
                "读取配置失败",
                $"无法读取 {name}：{ex.Message}");
        }

        if (bytes.LongLength > maxBytes)
        {
            throw TooLarge();
        }

        var hasBom = HasBom(bytes);
        var content = Decode(name, bytes, hasBom);
        return new NginxConfigContent(
            name,
            path,
            content,
            ToUnixMilliseconds(File.GetLastWriteTimeUtc(path)),
            bytes.LongLength,
            hasBom);
    }

    /// <summary>
    /// 保存一个配置文件：校验乐观并发版本、UTF-8 内容与大小上限，然后原子替换。
    /// 不执行 nginx 语法检测（由调用方按需单独触发）。
    /// </summary>
    public async Task<NginxConfigSaveResult> SaveAsync(
        NginxOptions settings,
        string name,
        string content,
        long expectedModifiedAt,
        bool hasBom,
        CancellationToken cancellationToken)
    {
        if (content.Contains('\0'))
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置内容不合法",
                "配置内容不能包含 NUL 字符。");
        }

        byte[] body;
        try
        {
            body = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置内容不合法",
                "配置内容必须是可以编码为 UTF-8 的文本。");
        }

        var maxBytes = MaxFileBytes(settings);
        var totalBytes = (long)body.Length + (hasBom ? Utf8Bom.Length : 0);
        if (totalBytes > maxBytes)
        {
            throw TooLarge();
        }

        var path = ResolveFile(settings, name);
        var file = new FileInfo(path);
        if (!file.Exists || IsLink(file))
        {
            throw NotFound(name);
        }

        if (expectedModifiedAt > 0)
        {
            var current = ToUnixMilliseconds(File.GetLastWriteTimeUtc(path));
            if (current != expectedModifiedAt)
            {
                throw new NginxConfigException(
                    StatusCodes.Status409Conflict,
                    "配置已被修改",
                    "该文件已被其他程序修改，请重新加载或确认覆盖。");
            }
        }

        UnixFileMode? mode = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                mode = File.GetUnixFileMode(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读取权限位失败不影响内容写入，沿用临时文件默认权限。
            }
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new NginxConfigException(StatusCodes.Status400BadRequest, "路径不合法", "无法确定配置文件的父目录。");
        // 临时文件不使用 .conf 后缀，即使残留也不会被 nginx include。
        var temporary = Path.Combine(directory, $".koi-nginx-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                System.IO.FileOptions.Asynchronous))
            {
                if (hasBom)
                {
                    await stream.WriteAsync(Utf8Bom, cancellationToken);
                }

                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (mode is { } value && OperatingSystem.IsLinux())
            {
                try
                {
                    File.SetUnixFileMode(temporary, value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 权限位恢复失败时仍完成保存，避免内容丢失。
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }

        var saved = new FileInfo(path);
        return new NginxConfigSaveResult(
            name,
            path,
            ToUnixMilliseconds(saved.LastWriteTimeUtc),
            saved.Length);
    }

    /// <summary>规范化并确认配置目录存在。</summary>
    private static string ResolveDirectory(NginxOptions settings)
    {
        var directory = Path.GetFullPath(settings.ConfDirectory);
        if (!Directory.Exists(directory))
        {
            throw new NginxConfigException(
                StatusCodes.Status404NotFound,
                "配置目录不存在",
                $"未找到 Nginx 配置目录 {directory}，请在 Nginx 管理设置中检查 conf.d 目录。");
        }

        return directory;
    }

    /// <summary>把配置文件名解析为目录内的完整路径，并再次校验目录包含关系。</summary>
    private static string ResolveFile(NginxOptions settings, string name)
    {
        if (string.IsNullOrEmpty(name) || !FileNamePattern.IsMatch(name))
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置文件名不合法",
                "文件名必须以字母、数字或下划线开头，只能包含字母、数字、点、下划线与连字符，并以 .conf 结尾。");
        }

        var directory = ResolveDirectory(settings);
        var path = Path.GetFullPath(Path.Combine(directory, name));

        // 文件名已限制为不含分隔符，这里仍做一次包含校验，防止未来放宽正则时出现穿越。
        var relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar))
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置文件名不合法",
                "配置文件必须位于 conf.d 目录的顶层。");
        }

        return path;
    }

    private string Decode(string name, byte[] bytes, bool hasBom)
    {
        var offset = hasBom ? Utf8Bom.Length : 0;
        string content;
        try
        {
            content = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置内容不是 UTF-8",
                $"文件 {name} 不是有效的 UTF-8 文本，无法在线编辑。");
        }

        if (content.Contains('\0'))
        {
            throw new NginxConfigException(
                StatusCodes.Status400BadRequest,
                "配置内容不合法",
                $"文件 {name} 包含 NUL 字符，无法在线编辑。");
        }

        return content;
    }

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= Utf8Bom.Length &&
        bytes[0] == Utf8Bom[0] &&
        bytes[1] == Utf8Bom[1] &&
        bytes[2] == Utf8Bom[2];

    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null ||
                   info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法判断时按链接处理，避免意外写入目录外文件。
            return true;
        }
    }

    private static long MaxFileBytes(NginxOptions settings) =>
        settings.MaxFileBytes > 0 ? settings.MaxFileBytes : 1024 * 1024;

    private static NginxConfigException NotFound(string name) =>
        new(StatusCodes.Status404NotFound, "配置文件不存在", $"未找到配置文件 {name}。");

    private static NginxConfigException TooLarge() =>
        new(
            StatusCodes.Status413PayloadTooLarge,
            "配置文件过大",
            "配置文件超过大小上限，无法在线读取或保存。");

    private static long ToUnixMilliseconds(DateTime utc) =>
        new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不影响保存结果。
        }
    }
}
