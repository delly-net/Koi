/**
 * 图片浏览支持的扩展名。与后端 `api/Files/ImageFileTypes.cs` 的后缀列表保持一致：
 * SVG/AVIF 可全屏查看原图，但 ImageSharp 无法生成缩略图，会显示占位图。
 */
export const IMAGE_EXTENSIONS = [
  '.jpg',
  '.jpeg',
  '.png',
  '.gif',
  '.webp',
  '.bmp',
  '.svg',
  '.ico',
  '.avif',
  '.tif',
  '.tiff',
] as const

/**
 * 可由后端 ImageSharp 解码并生成缩略图的后缀。与 `api/Files/ImageFileTypes.cs` 的
 * `ThumbnailDecodable` 保持一致：SVG/AVIF 只能查看原图，缩略图接口会返回 415。
 */
export const THUMBNAIL_EXTENSIONS = [
  '.jpg',
  '.jpeg',
  '.png',
  '.gif',
  '.webp',
  '.bmp',
  '.ico',
  '.tif',
  '.tiff',
] as const

const IMAGE_EXTENSION_SET = new Set<string>(IMAGE_EXTENSIONS)
const THUMBNAIL_EXTENSION_SET = new Set<string>(THUMBNAIL_EXTENSIONS)

/** 取文件名的小写后缀（含点）；没有后缀时返回空字符串。 */
function extensionOf(nameOrPath: string): string {
  const name = nameOrPath.replace(/\\/g, '/')
  const slash = name.lastIndexOf('/')
  const fileName = slash >= 0 ? name.slice(slash + 1) : name
  const dot = fileName.lastIndexOf('.')
  return dot >= 0 ? fileName.slice(dot).toLowerCase() : ''
}

/** 文件名或路径是否属于受支持的图片类型（不区分大小写）。 */
export function isImageFile(nameOrPath: string): boolean {
  const extension = extensionOf(nameOrPath)
  return extension !== '' && IMAGE_EXTENSION_SET.has(extension)
}

/**
 * 能否复用「图片浏览」的缩略图接口。SVG/AVIF 等浏览器可显示但后端无法解码的类型返回
 * false，调用方应回退为通用图片图标，避免必然失败的 415 请求。
 */
export function hasImageThumbnail(nameOrPath: string): boolean {
  const extension = extensionOf(nameOrPath)
  return extension !== '' && THUMBNAIL_EXTENSION_SET.has(extension)
}
