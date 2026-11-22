// Unix 权限位 API 只在 Linux 辅助进程中调用；分析器无法跨越进程边界推断平台。
#pragma warning disable CA1416

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace Koi.Api.Files;

/// <summary>
/// 文件操作实现。本类只在文件辅助进程中运行——此时进程已经降权到登录用户，
/// 因此所有文件系统调用天然受该用户的权限、配额与 ACL 约束。
/// </summary>
public static class FileOperations
{
    private const int CopyBufferSize = 81920;
    private const int MaxBatchItems = 1000;

    /// <summary>递归图片扫描允许的最大子目录深度。</summary>
    private const int ImageScanMaxDepth = 16;

    /// <summary>递归图片扫描最多访问的目录条目数，防止超大目录树长时间占用辅助进程。</summary>
    private const int ImageScanMaxVisitedEntries = 50_000;

    private static readonly UnixFileMode[] ModeBits =
    [
        UnixFileMode.UserRead,
        UnixFileMode.UserWrite,
        UnixFileMode.UserExecute,
        UnixFileMode.GroupRead,
        UnixFileMode.GroupWrite,
        UnixFileMode.GroupExecute,
        UnixFileMode.OtherRead,
        UnixFileMode.OtherWrite,
        UnixFileMode.OtherExecute,
    ];

    /// <summary>
    /// 执行非下载类操作（列表/新建/重命名/删除/复制/移动/上传）。
    /// <paramref name="progress"/> 仅复制/移动时使用：把进度帧写入标准输出，主进程据此更新快照。
    /// </summary>
    public static async Task<FileHelperResponse> ExecuteAsync(
        FileHelperRequest request,
        Stream input,
        CancellationToken cancellationToken,
        HelperTransferProgressWriter? progress = null)
    {
        try
        {
            return request.Op switch
            {
                "list" => List(request),
                "imagelist" => ListImages(request),
                "mkdir" => MakeDirectory(request),
                "touch" => CreateFile(request),
                "rename" => Rename(request),
                "delete" => Delete(request),
                "copy" => Transfer(request, move: false, progress),
                "move" => Transfer(request, move: true, progress),
                "upload" => await UploadAsync(request, input, cancellationToken),
                "writetext" => await WriteTextAsync(request, input, cancellationToken),
                _ => throw new FileOperationException(FileErrorCodes.InvalidPath, "不支持的操作。"),
            };
        }
        catch (FileOperationException ex)
        {
            return FileHelperResponse.Failure(ex.Code, ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.PermissionDenied, "没有访问该路径的权限。");
        }
        catch (DirectoryNotFoundException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.NotFound, "目录不存在。");
        }
        catch (FileNotFoundException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.NotFound, "文件不存在。");
        }
        catch (IOException ex)
        {
            return FileHelperResponse.Failure(
                FileErrorCodes.IoError,
                string.IsNullOrWhiteSpace(ex.Message) ? "文件操作失败。" : ex.Message);
        }
    }

    /// <summary>打开下载文件；响应头之后由调用方把文件流原样写入标准输出。</summary>
    public static (FileHelperResponse Response, FileStream? Stream) OpenDownload(FileHelperRequest request)
    {
        try
        {
            var path = RequirePath(request, request.Path);
            if (IsSymlink(path))
            {
                if (Directory.Exists(path))
                {
                    throw new FileOperationException(FileErrorCodes.IsDirectory, "目标是目录，无法下载。");
                }
            }
            else if (Directory.Exists(path))
            {
                throw new FileOperationException(FileErrorCodes.IsDirectory, "目标是目录，无法下载。");
            }

            if (!File.Exists(path))
            {
                throw new FileOperationException(FileErrorCodes.NotFound, "文件不存在。");
            }

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize,
                System.IO.FileOptions.Asynchronous | System.IO.FileOptions.SequentialScan);

            return (
                new FileHelperResponse
                {
                    Ok = true,
                    Size = stream.Length,
                    Name = Path.GetFileName(path),
                    ModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(),
                },
                stream);
        }
        catch (FileOperationException ex)
        {
            return (FileHelperResponse.Failure(ex.Code, ex.Message), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (FileHelperResponse.Failure(FileErrorCodes.PermissionDenied, "没有读取该文件的权限。"), null);
        }
        catch (IOException ex)
        {
            return (FileHelperResponse.Failure(FileErrorCodes.IoError, ex.Message), null);
        }
    }

    private static FileHelperResponse List(FileHelperRequest request)
    {
        var path = RequirePath(request, request.Path);
        if (!Directory.Exists(path))
        {
            throw new FileOperationException(
                File.Exists(path) ? FileErrorCodes.NotDirectory : FileErrorCodes.NotFound,
                "目标目录不存在。");
        }

        var limit = Math.Clamp(request.MaxEntries <= 0 ? 5000 : request.MaxEntries, 1, 100_000);
        var entries = new List<FileEntryInfo>(Math.Min(limit, 512));
        var truncated = false;

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if (entries.Count >= limit)
            {
                truncated = true;
                break;
            }

            entries.Add(Describe(entry));
        }

        entries.Sort((left, right) =>
        {
            if (left.IsDirectory != right.IsDirectory)
            {
                return left.IsDirectory ? -1 : 1;
            }

            return string.CompareOrdinal(left.Name, right.Name);
        });

        return new FileHelperResponse
        {
            Ok = true,
            List = new FileListPayload(path, Path.GetDirectoryName(path), entries, truncated),
        };
    }

    /// <summary>
    /// 递归扫描目录下的所有图片。只返回普通文件；符号链接/重解析点（含目录链接）全部跳过，
    /// 避免环路与越界访问。达到数量、深度或访问条目上限时置 <c>Truncated</c>。
    /// </summary>
    private static FileHelperResponse ListImages(FileHelperRequest request)
    {
        var path = RequirePath(request, request.Path);
        if (!Directory.Exists(path))
        {
            throw new FileOperationException(
                File.Exists(path) ? FileErrorCodes.NotDirectory : FileErrorCodes.NotFound,
                "目标目录不存在。");
        }

        var limit = Math.Clamp(request.MaxEntries <= 0 ? 5000 : request.MaxEntries, 1, 100_000);
        var images = new List<FileImageEntryInfo>(Math.Min(limit, 512));
        var truncated = false;
        var stopped = false;
        var visited = 0;

        // 显式栈而非递归，避免极深目录树压爆调用栈。
        var pending = new Stack<(string Directory, string Relative, int Depth)>();
        pending.Push((path, string.Empty, 0));

        while (pending.Count > 0 && images.Count < limit && !stopped)
        {
            var (directory, relative, depth) = pending.Pop();

            IEnumerator<FileSystemInfo>? enumerator;
            try
            {
                enumerator = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (Exception ex) when (IsSkippableFileSystemError(ex))
            {
                // 单个子目录不可读时跳过，不影响其余目录。
                continue;
            }

            using (enumerator)
            {
                while (true)
                {
                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        entry = enumerator.Current;
                    }
                    catch (Exception ex) when (IsSkippableFileSystemError(ex))
                    {
                        // 枚举过程中目录被删除或权限变化：结束当前目录。
                        break;
                    }

                    visited++;
                    if (visited > ImageScanMaxVisitedEntries)
                    {
                        truncated = true;
                        stopped = true;
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception ex) when (IsSkippableFileSystemError(ex))
                    {
                        continue;
                    }

                    // 跳过符号链接/重解析点（避免环路与越界）以及设备文件。
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        continue;
                    }

                    var name = entry.Name;
                    var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // 超出深度上限的分支不再深入，但继续扫描其它同级目录。
                        if (depth >= ImageScanMaxDepth)
                        {
                            truncated = true;
                            continue;
                        }

                        pending.Push((entry.FullName, childRelative, depth + 1));
                        continue;
                    }

                    if (!ImageFileTypes.IsImage(name))
                    {
                        continue;
                    }

                    long size;
                    long modifiedAt;
                    try
                    {
                        var file = (FileInfo)entry;
                        size = file.Length;
                        modifiedAt = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                    }
                    catch (Exception ex) when (IsSkippableFileSystemError(ex))
                    {
                        continue;
                    }

                    images.Add(new FileImageEntryInfo(
                        name,
                        entry.FullName,
                        childRelative,
                        size,
                        modifiedAt));

                    if (images.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
        }

        // 排序保证同一目录树的结果稳定，前端翻页顺序可预期。
        images.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        return new FileHelperResponse
        {
            Ok = true,
            Images = new FileImageListPayload(path, images, truncated),
        };
    }

    /// <summary>
    /// 生成 128×128 以内的 PNG 缩略图。必须在降权后的辅助进程中调用，
    /// 这样解码用户图片受该用户的文件权限约束。
    /// </summary>
    public static FileHelperResponse CreateThumbnail(FileHelperRequest request, out byte[]? content)
    {
        content = null;

        try
        {
            var path = RequirePath(request, request.Path);
            if (Directory.Exists(path))
            {
                throw new FileOperationException(FileErrorCodes.IsDirectory, "目标是目录，无法生成缩略图。");
            }

            if (!File.Exists(path))
            {
                throw new FileOperationException(FileErrorCodes.NotFound, "文件不存在。");
            }

            if (!ImageFileTypes.CanThumbnail(path))
            {
                throw new FileOperationException(
                    FileErrorCodes.UnsupportedImage,
                    "该图片格式不支持生成缩略图。");
            }

            if ((File.GetAttributes(path) & FileAttributes.Device) != 0)
            {
                throw new FileOperationException(
                    FileErrorCodes.UnsupportedImage,
                    "设备文件无法生成缩略图。");
            }

            var file = new FileInfo(path);
            if (file.Length > ImageFileTypes.MaxThumbnailSourceBytes)
            {
                throw new FileOperationException(FileErrorCodes.TooLarge, "图片文件过大，无法生成缩略图。");
            }

            // 先读图片头判断尺寸，避免超大图像在解码阶段耗尽内存。
            var imageInfo = Image.Identify(path);
            if (imageInfo is null)
            {
                throw new FileOperationException(FileErrorCodes.UnsupportedImage, "无法识别图片格式。");
            }

            if ((long)imageInfo.Width * imageInfo.Height > ImageFileTypes.MaxThumbnailSourcePixels)
            {
                throw new FileOperationException(FileErrorCodes.TooLarge, "图片像素数过大，无法生成缩略图。");
            }

            // 动图只取首帧，避免把整段动画解码进内存；AutoOrient 让缩略图方向与浏览器一致。
            using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, path);
            image.Mutate(context => context
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Size = new Size(ImageFileTypes.ThumbnailSize, ImageFileTypes.ThumbnailSize),
                    Mode = ResizeMode.Max,
                }));

            using var buffer = new MemoryStream();
            image.SaveAsPng(buffer);
            content = buffer.ToArray();

            return new FileHelperResponse
            {
                Ok = true,
                Size = content.LongLength,
                ModifiedAt = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            };
        }
        catch (FileOperationException ex)
        {
            return FileHelperResponse.Failure(ex.Code, ex.Message);
        }
        catch (UnknownImageFormatException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.UnsupportedImage, "该图片格式不支持生成缩略图。");
        }
        catch (InvalidImageContentException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.UnsupportedImage, "图片内容损坏，无法生成缩略图。");
        }
        catch (UnauthorizedAccessException)
        {
            return FileHelperResponse.Failure(FileErrorCodes.PermissionDenied, "没有读取该图片的权限。");
        }
        catch (IOException ex)
        {
            return FileHelperResponse.Failure(FileErrorCodes.IoError, ex.Message);
        }
    }

    /// <summary>递归枚举时可以安全跳过、不应中止整个扫描的文件系统错误。</summary>
    private static bool IsSkippableFileSystemError(Exception ex) =>
        ex is UnauthorizedAccessException
            or IOException
            or PathTooLongException
            or System.Security.SecurityException;

    private static FileHelperResponse MakeDirectory(FileHelperRequest request)
    {
        var path = RequirePath(request, request.Path);
        if (Directory.Exists(path) || File.Exists(path) || IsSymlink(path))
        {
            throw new FileOperationException(FileErrorCodes.AlreadyExists, "同名文件或目录已存在。");
        }

        Directory.CreateDirectory(path);
        return FileHelperResponse.Success();
    }

    private static FileHelperResponse CreateFile(FileHelperRequest request)
    {
        var path = RequirePath(request, request.Path);
        if (Directory.Exists(path) || File.Exists(path) || IsSymlink(path))
        {
            throw new FileOperationException(FileErrorCodes.AlreadyExists, "同名文件或目录已存在。");
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return FileHelperResponse.Success();
    }

    private static FileHelperResponse Rename(FileHelperRequest request)
    {
        var path = RequirePath(request, request.Path);
        if (!FilePaths.IsValidName(request.NewName))
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "名称不合法。");
        }

        if (!Exists(path))
        {
            throw new FileOperationException(FileErrorCodes.NotFound, "要重命名的文件或目录不存在。");
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new FileOperationException(FileErrorCodes.InvalidPath, "无法确定父目录。");
        var destination = RequirePath(request, Path.Combine(directory, request.NewName!));

        if (string.Equals(path, destination, StringComparison.Ordinal))
        {
            return FileHelperResponse.Success();
        }

        if (Exists(destination))
        {
            throw new FileOperationException(FileErrorCodes.AlreadyExists, "目标名称已存在。");
        }

        if (IsSymlink(path) || !Directory.Exists(path))
        {
            File.Move(path, destination);
        }
        else
        {
            Directory.Move(path, destination);
        }

        return FileHelperResponse.Success();
    }

    private static FileHelperResponse Delete(FileHelperRequest request)
    {
        var sources = request.Sources ?? [];
        if (sources.Length == 0)
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "未指定要删除的路径。");
        }

        if (sources.Length > MaxBatchItems)
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "单次删除的条目过多。");
        }

        var paths = sources.Select(source => RequirePath(request, source)).ToArray();
        foreach (var path in paths)
        {
            if (IsProtectedRoot(path, request))
            {
                throw new FileOperationException(FileErrorCodes.InvalidPath, "不允许删除根目录。");
            }
        }

        foreach (var path in paths)
        {
            if (IsSymlink(path) || File.Exists(path))
            {
                // 符号链接只删除链接本身，不触碰目标。
                File.Delete(path);
                continue;
            }

            if (!Directory.Exists(path))
            {
                throw new FileOperationException(FileErrorCodes.NotFound, $"路径不存在：{path}");
            }

            if (!request.Recursive && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new FileOperationException(FileErrorCodes.NotEmpty, "目录非空，需要确认递归删除。");
            }

            Directory.Delete(path, request.Recursive);
        }

        return FileHelperResponse.Success();
    }

    private static FileHelperResponse Transfer(
        FileHelperRequest request,
        bool move,
        HelperTransferProgressWriter? progress)
    {
        var sources = request.Sources ?? [];
        if (sources.Length == 0)
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "未指定源路径。");
        }

        if (sources.Length > MaxBatchItems)
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "单次操作的条目过多。");
        }

        var target = RequirePath(request, request.Target);
        if (!Directory.Exists(target))
        {
            throw new FileOperationException(FileErrorCodes.NotDirectory, "目标目录不存在。");
        }

        // 第一段：先校验全部源并确定目标路径。统计总进度需要先知道完整清单，
        // 所有参数错误都在复制任何条目之前暴露。
        var items = new List<(string Source, string Destination)>(sources.Length);
        foreach (var source in sources.Select(source => RequirePath(request, source)))
        {
            if (!Exists(source))
            {
                throw new FileOperationException(FileErrorCodes.NotFound, $"源路径不存在：{source}");
            }

            var name = Path.GetFileName(source.TrimEnd('/'));
            if (!FilePaths.IsValidName(name))
            {
                throw new FileOperationException(FileErrorCodes.InvalidPath, "无法确定源路径的名称。");
            }

            if (Directory.Exists(source) && !IsSymlink(source) && IsSameOrInside(target, source))
            {
                throw new FileOperationException(FileErrorCodes.InvalidPath, "不能把目录复制或移动到其自身内部。");
            }

            var destination = RequirePath(request, Path.Combine(target, name));
            if (string.Equals(source, destination, StringComparison.Ordinal))
            {
                throw new FileOperationException(FileErrorCodes.InvalidPath, "源与目标相同。");
            }

            items.Add((source, destination));
        }

        // 第二段：统计每个源的文件数与字节数（符号链接不跟随、按条目计、字节计 0），
        // 让前端在复制开始前就知道总进度。统计期间上报已扫描数量，前端显示不确定进度。
        progress?.BeginScanning();
        var counts = new (long Bytes, int Entries)[items.Count];
        long bytesTotal = 0;
        int entriesTotal = 0;
        int scannedEntries = 0;
        long scannedBytes = 0;
        for (var index = 0; index < items.Count; index++)
        {
            counts[index] = CountEntry(
                items[index].Source,
                ref scannedEntries,
                ref scannedBytes,
                progress);
            bytesTotal += counts[index].Bytes;
            entriesTotal += counts[index].Entries;
        }

        progress?.StartTransfer(bytesTotal, entriesTotal);

        // 第三段：逐项删除覆盖目标并复制/移动。
        for (var index = 0; index < items.Count; index++)
        {
            var (source, destination) = items[index];
            if (Exists(destination))
            {
                if (!request.Overwrite)
                {
                    throw new FileOperationException(FileErrorCodes.AlreadyExists, $"目标已存在：{Path.GetFileName(destination)}");
                }

                DeleteEntry(destination);
            }

            if (move)
            {
                MoveEntry(source, destination, counts[index], progress);
            }
            else
            {
                CopyEntry(source, destination, progress);
            }
        }

        progress?.Done();
        return FileHelperResponse.Success();
    }

    /// <summary>
    /// 统计一个源子树中的文件数与字节数（目录本身不计入条目，符号链接按 1 个条目计）。
    /// 读取文件大小失败时按 0 字节计，避免统计阶段掩盖后续复制的真实错误。
    /// </summary>
    private static (long Bytes, int Entries) CountEntry(
        string path,
        ref int scannedEntries,
        ref long scannedBytes,
        HelperTransferProgressWriter? progress)
    {
        long bytes = 0;
        int entries = 0;
        AccumulateEntry(path, ref bytes, ref entries, ref scannedEntries, ref scannedBytes, progress);
        return (bytes, entries);
    }

    private static void AccumulateEntry(
        string path,
        ref long bytes,
        ref int entries,
        ref int scannedEntries,
        ref long scannedBytes,
        HelperTransferProgressWriter? progress)
    {
        if (IsSymlink(path))
        {
            entries += 1;
            scannedEntries += 1;
            progress?.Scanned(scannedEntries, scannedBytes);
            return;
        }

        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                AccumulateEntry(child, ref bytes, ref entries, ref scannedEntries, ref scannedBytes, progress);
            }

            return;
        }

        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 统计阶段按 0 计；复制阶段会给出真实错误。
        }

        entries += 1;
        bytes += size;
        scannedEntries += 1;
        scannedBytes += size;
        progress?.Scanned(scannedEntries, scannedBytes);
    }

    private static async Task<FileHelperResponse> UploadAsync(
        FileHelperRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        var directory = RequirePath(request, request.Path);
        if (!Directory.Exists(directory))
        {
            throw new FileOperationException(FileErrorCodes.NotDirectory, "上传目标目录不存在。");
        }

        var name = Path.GetFileName(request.NewName ?? string.Empty);
        if (!FilePaths.IsValidName(name))
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "文件名不合法。");
        }

        var destination = RequirePath(request, Path.Combine(directory, name));
        if (Directory.Exists(destination))
        {
            throw new FileOperationException(FileErrorCodes.IsDirectory, "目标位置存在同名目录。");
        }

        if (Exists(destination))
        {
            if (!request.Overwrite)
            {
                throw new FileOperationException(FileErrorCodes.AlreadyExists, "目标文件已存在。");
            }

            File.Delete(destination);
        }

        // 先写同目录临时文件再改名，避免上传中断留下半截目标文件。
        var temporary = Path.Combine(directory, $".koi-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                System.IO.FileOptions.Asynchronous))
            {
                var buffer = new byte[CopyBufferSize];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (request.MaxBytes > 0 && total > request.MaxBytes)
                    {
                        throw new FileOperationException(FileErrorCodes.TooLarge, "上传文件超过大小限制。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            TryDelete(temporary);
        }

        return FileHelperResponse.Success();
    }

    /// <summary>
    /// 保存在线编辑的文本内容：请求头之后是 UTF-8 原始字节。
    /// 先写同目录临时文件并保留原权限位，再原子改名，避免中断留下半截文件。
    /// </summary>
    private static async Task<FileHelperResponse> WriteTextAsync(
        FileHelperRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        var path = RequirePath(request, request.Path);
        if (Directory.Exists(path))
        {
            throw new FileOperationException(FileErrorCodes.IsDirectory, "目标是目录，无法保存文本。");
        }

        // 符号链接写入其指向的真实文件，避免把链接本身替换成普通文件。
        if (IsSymlink(path))
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new FileOperationException(FileErrorCodes.NotFound, "符号链接目标不存在。");
            path = RequirePath(request, target.FullName);
        }

        if (!File.Exists(path))
        {
            throw new FileOperationException(FileErrorCodes.NotFound, "文件不存在，无法保存。");
        }

        if (request.ExpectedModifiedAt > 0)
        {
            var current = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
            if (current != request.ExpectedModifiedAt)
            {
                throw new FileOperationException(
                    FileErrorCodes.Conflict,
                    "文件已被其他程序修改，请重新加载或确认覆盖。");
            }
        }

        UnixFileMode? mode = null;
        try
        {
            mode = File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取权限位失败不影响内容写入，沿用临时文件默认权限。
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new FileOperationException(FileErrorCodes.InvalidPath, "无法确定父目录。");
        var temporary = Path.Combine(directory, $".koi-edit-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                System.IO.FileOptions.Asynchronous))
            {
                var buffer = new byte[CopyBufferSize];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (request.MaxBytes > 0 && total > request.MaxBytes)
                    {
                        throw new FileOperationException(FileErrorCodes.TooLarge, "文本内容超过大小限制。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            if (mode is { } value)
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

        var info = new FileInfo(path);
        return new FileHelperResponse
        {
            Ok = true,
            Size = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        };
    }

    private static FileEntryInfo Describe(FileSystemInfo info)
    {
        var path = info.FullName;

        FileAttributes attributes;
        try
        {
            // Attributes / Length / LastWriteTimeUtc / UnixFileMode 复用同一次 lstat 结果，
            // 避免旧实现对每个条目重复 stat（慢盘或网络文件系统上差异显著）。
            attributes = info.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = FileAttributes.Normal;
        }

        var isSymlink = attributes.HasFlag(FileAttributes.ReparsePoint);

        // 非符号链接直接使用枚举结果的类型；符号链接才额外跟随目标判断是否为目录。
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isSymlink)
        {
            isDirectory = Directory.Exists(path);
        }

        long size = 0;
        if (!isDirectory && info is FileInfo fileInfo)
        {
            try
            {
                size = fileInfo.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = 0;
            }
        }

        long modifiedAt = 0;
        try
        {
            modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            modifiedAt = 0;
        }

        string mode;
        try
        {
            mode = FormatMode(info.UnixFileMode, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mode = string.Empty;
        }

        return new FileEntryInfo(info.Name, path, isDirectory, isSymlink, size, modifiedAt, mode);
    }

    private static string FormatMode(UnixFileMode mode, bool isDirectory)
    {
        var chars = new char[10];
        chars[0] = isDirectory ? 'd' : '-';
        for (var index = 0; index < ModeBits.Length; index++)
        {
            chars[index + 1] = mode.HasFlag(ModeBits[index]) ? "rwx"[index % 3] : '-';
        }

        return new string(chars);
    }

    private static void CopyEntry(
        string source,
        string destination,
        HelperTransferProgressWriter? progress)
    {
        if (IsSymlink(source))
        {
            var linkTarget = new FileInfo(source).LinkTarget;
            if (linkTarget is null)
            {
                throw new FileOperationException(FileErrorCodes.IoError, "无法读取符号链接目标。");
            }

            if (Directory.Exists(source))
            {
                Directory.CreateSymbolicLink(destination, linkTarget);
            }
            else
            {
                File.CreateSymbolicLink(destination, linkTarget);
            }

            progress?.AddEntry(Path.GetFileName(source), 0);
            return;
        }

        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                CopyEntry(child, Path.Combine(destination, Path.GetFileName(child)), progress);
            }

            TryCopyMode(source, destination);
            return;
        }

        // 用分块流式复制替代 File.Copy：按块累计字节并上报进度。
        // 完成后仍复制权限位与时间戳，保持与 File.Copy 接近的元数据行为。
        using (var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize))
        using (var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize))
        {
            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                progress?.AddBytes(read);
            }
        }

        TryCopyMode(source, destination);
        TryCopyTimestamps(source, destination);
        progress?.AddEntry(Path.GetFileName(source), 0);
    }

    private static void MoveEntry(
        string source,
        string destination,
        (long Bytes, int Entries) counts,
        HelperTransferProgressWriter? progress)
    {
        try
        {
            if (Directory.Exists(source) && !IsSymlink(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }

            // 同设备移动是常数时间操作，整棵源子树一瞬间完成。
            progress?.AddCompleted(counts.Bytes, counts.Entries);
        }
        catch (IOException) when (!Exists(destination))
        {
            // 跨设备（EXDEV）等场景回退为复制 + 删除；复制过程继续上报字节进度。
            CopyEntry(source, destination, progress);
            DeleteEntry(source);
        }
    }

    private static void DeleteEntry(string path)
    {
        if (IsSymlink(path) || File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryCopyMode(string source, string destination)
    {
        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 权限位复制失败不影响文件内容，忽略。
        }
    }

    /// <summary>复制最后修改/访问时间，贴近 File.Copy 的元数据行为；失败不影响内容。</summary>
    private static void TryCopyTimestamps(string source, string destination)
    {
        try
        {
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            File.SetLastAccessTimeUtc(destination, File.GetLastAccessTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 时间戳复制失败属非致命问题，忽略。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不覆盖原始错误。
        }
    }

    private static string RequirePath(FileHelperRequest request, string? value)
    {
        if (!FilePaths.TryNormalize(value, out var path))
        {
            throw new FileOperationException(FileErrorCodes.InvalidPath, "路径不合法。");
        }

        if (request.Roots is { Length: > 0 } roots &&
            !FilePaths.IsWithinRoots(FilePaths.ResolveRealPath(path), roots))
        {
            throw new FileOperationException(FileErrorCodes.PermissionDenied, "路径超出允许访问的范围。");
        }

        return path;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsSymlink(path);

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSameOrInside(string path, string directory) =>
        string.Equals(path, directory, StringComparison.Ordinal) ||
        path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static bool IsProtectedRoot(string path, FileHelperRequest request)
    {
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return true;
        }

        return (request.Roots ?? []).Any(root =>
            FilePaths.TryNormalize(root, out var normalizedRoot) &&
            string.Equals(path, normalizedRoot.TrimEnd('/'), StringComparison.Ordinal));
    }
}
