// Unix 权限位 API 只在 Linux 辅助进程中调用；分析器无法跨越进程边界推断平台。
#pragma warning disable CA1416

namespace Koi.Api.Files;

/// <summary>
/// 文件操作实现。本类只在文件辅助进程中运行——此时进程已经降权到登录用户，
/// 因此所有文件系统调用天然受该用户的权限、配额与 ACL 约束。
/// </summary>
public static class FileOperations
{
    private const int CopyBufferSize = 81920;
    private const int MaxBatchItems = 1000;

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

    /// <summary>执行非下载类操作（列表/新建/重命名/删除/复制/移动/上传）。</summary>
    public static async Task<FileHelperResponse> ExecuteAsync(
        FileHelperRequest request,
        Stream input,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Op switch
            {
                "list" => List(request),
                "mkdir" => MakeDirectory(request),
                "touch" => CreateFile(request),
                "rename" => Rename(request),
                "delete" => Delete(request),
                "copy" => Transfer(request, move: false),
                "move" => Transfer(request, move: true),
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

    private static FileHelperResponse Transfer(FileHelperRequest request, bool move)
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

            if (Exists(destination))
            {
                if (!request.Overwrite)
                {
                    throw new FileOperationException(FileErrorCodes.AlreadyExists, $"目标已存在：{name}");
                }

                DeleteEntry(destination);
            }

            if (move)
            {
                MoveEntry(source, destination);
            }
            else
            {
                CopyEntry(source, destination);
            }
        }

        return FileHelperResponse.Success();
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

    private static void CopyEntry(string source, string destination)
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

            return;
        }

        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                CopyEntry(child, Path.Combine(destination, Path.GetFileName(child)));
            }

            TryCopyMode(source, destination);
            return;
        }

        File.Copy(source, destination, overwrite: false);
        TryCopyMode(source, destination);
    }

    private static void MoveEntry(string source, string destination)
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
        }
        catch (IOException) when (!Exists(destination))
        {
            // 跨设备（EXDEV）等场景回退为复制 + 删除。
            CopyEntry(source, destination);
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
