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

const IMAGE_EXTENSION_SET = new Set<string>(IMAGE_EXTENSIONS)

/** 文件名或路径是否属于受支持的图片类型（不区分大小写）。 */
export function isImageFile(nameOrPath: string): boolean {
  const name = nameOrPath.replace(/\\/g, '/')
  const slash = name.lastIndexOf('/')
  const fileName = slash >= 0 ? name.slice(slash + 1) : name
  const dot = fileName.lastIndexOf('.')
  if (dot < 0) {
    return false
  }

  return IMAGE_EXTENSION_SET.has(fileName.slice(dot).toLowerCase())
}
