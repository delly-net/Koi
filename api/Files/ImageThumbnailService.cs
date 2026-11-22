using System.Security.Cryptography;
using System.Text;

namespace Koi.Api.Files;

/// <summary>
/// 图片缩略图的磁盘缓存。缩略图由降权后的文件辅助进程生成，主进程只负责
/// 写入缓存并向浏览器输出；缓存目录是纯缓存，删除后会自动重建。
/// </summary>
public sealed class ImageThumbnailService(
    FileOperationService operations,
    ILogger<ImageThumbnailService> logger)
{
    /// <summary>同时生成缩略图的辅助进程上限，避免大量图片同时解码拖垮主机。</summary>
    private const int MaxConcurrentGenerations = 4;

    /// <summary>一张 128×128 PNG 缩略图的字节上限；超过视为异常输出。</summary>
    private const long MaxThumbnailBytes = 2 * 1024 * 1024;

    /// <summary>缓存条目上限；超过后删除最旧的文件。</summary>
    private const int MaxCacheEntries = 20_000;

    /// <summary>缓存最长保留时间。</summary>
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _gate = new(MaxConcurrentGenerations, MaxConcurrentGenerations);

    /// <summary>缩略图缓存目录：程序目录下的 <c>thumbnails</c>。</summary>
    public string CacheDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "thumbnails");

    /// <summary>
    /// 计算缓存文件路径：SHA-256(账号 + 绝对路径) + 版本号（通常为图片 mtime）。
    /// 缓存按账号隔离，避免一个账号命中另一个账号生成的缩略图而绕过降权后的文件权限；
    /// 版本号变化会让 URL 与缓存文件一起变化，从而避免命中旧缩略图。
    /// </summary>
    public string GetCachePath(string path, string? version, string scope)
    {
        var normalizedVersion = NormalizeVersion(version);
        var key = $"{scope}\n{path}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{hash}-{normalizedVersion}.png");
    }

    /// <summary>缓存文件存在且非空时返回 <c>true</c>。</summary>
    public bool TryGetCached(string cachePath)
    {
        try
        {
            return File.Exists(cachePath) && new FileInfo(cachePath).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成并写入缓存；并发受限。缓存命中时直接返回成功，不启动辅助进程。
    /// </summary>
    public async Task<FileHelperResponse> CreateAsync(
        FileHelperRequest request,
        string cachePath,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 等待信号量期间可能已由另一个请求完成生成。
            if (TryGetCached(cachePath))
            {
                return FileHelperResponse.Success();
            }

            var (response, content) = await operations.ReadThumbnailAsync(
                request,
                MaxThumbnailBytes,
                cancellationToken);

            if (!response.Ok)
            {
                return response;
            }

            if (content is not { Length: > 0 })
            {
                return FileHelperResponse.Failure(
                    FileErrorCodes.IoError,
                    "文件辅助进程没有返回缩略图内容。");
            }

            Directory.CreateDirectory(CacheDirectory);
            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, content, cancellationToken);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return FileHelperResponse.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "写入缩略图缓存失败：{Path}", cachePath);
            return FileHelperResponse.Failure(
                FileErrorCodes.IoError,
                "缩略图缓存写入失败。");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 尽力清理过期与超出上限的缓存。启动时执行一次；失败只记录警告，不影响服务。
    /// </summary>
    public void PruneOldEntries()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return;
            }

            var directory = new DirectoryInfo(CacheDirectory);
            var cutoff = DateTime.UtcNow - MaxCacheAge;
            var kept = new List<(string Path, DateTime LastWrite)>();
            var removed = 0;

            foreach (var file in directory.EnumerateFiles())
            {
                var isTemp = file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
                if (file.LastWriteTimeUtc < cutoff)
                {
                    TryDelete(file.FullName);
                    removed++;
                    continue;
                }

                if (!isTemp)
                {
                    kept.Add((file.FullName, file.LastWriteTimeUtc));
                }
            }

            if (kept.Count > MaxCacheEntries)
            {
                foreach (var item in kept
                             .OrderBy(entry => entry.LastWrite)
                             .Take(kept.Count - MaxCacheEntries))
                {
                    TryDelete(item.Path);
                    removed++;
                }
            }

            if (removed > 0)
            {
                logger.LogInformation("已清理 {Count} 个缩略图缓存文件。", removed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "清理缩略图缓存失败。");
        }
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 20)
        {
            return "0";
        }

        foreach (var ch in version)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return "0";
            }
        }

        return version;
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
            // 纯缓存，删除失败可忽略。
        }
    }
}
