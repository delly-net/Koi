namespace Koi.Api.Files;

/// <summary>
/// 图片浏览支持的文件类型。后缀列表与前端
/// <c>ui/src/config/imageTypes.ts</c> 中的 <c>IMAGE_EXTENSIONS</c> 保持一致。
/// </summary>
public static class ImageFileTypes
{
    /// <summary>缩略图的最大边长（像素），缩略图按等比缩放不超过该尺寸。</summary>
    public const int ThumbnailSize = 128;

    /// <summary>允许生成缩略图的源文件大小上限（64 MiB）。</summary>
    public const long MaxThumbnailSourceBytes = 64L * 1024 * 1024;

    /// <summary>允许生成缩略图的源图像像素数上限（8000 万），用于防止超大图耗尽内存。</summary>
    public const long MaxThumbnailSourcePixels = 80_000_000;

    /// <summary>后缀 → 响应 Content-Type。</summary>
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".avif"] = "image/avif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

    /// <summary>
    /// 可由 ImageSharp 解码并生成缩略图的后缀。
    /// SVG/AVIF 浏览器可显示，但 ImageSharp 不支持解码，只返回占位缩略图。
    /// </summary>
    private static readonly HashSet<string> ThumbnailDecodable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".bmp",
        ".ico",
        ".tif",
        ".tiff",
    };

    /// <summary>文件名是否为受支持的图片类型。</summary>
    public static bool IsImage(string name) =>
        MimeTypes.ContainsKey(Path.GetExtension(name));

    /// <summary>取图片后缀对应的 Content-Type；非图片返回 <c>null</c>。</summary>
    public static string? GetContentType(string name) =>
        MimeTypes.TryGetValue(Path.GetExtension(name), out var contentType) ? contentType : null;

    /// <summary>该后缀能否由 ImageSharp 生成缩略图。</summary>
    public static bool CanThumbnail(string name) =>
        ThumbnailDecodable.Contains(Path.GetExtension(name));
}
