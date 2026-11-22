/** 文件大小的可读表示；文件列表与粘贴进度等位置共用同一口径。 */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`
}

/** 修改时间的本地化展示；0 表示该条目的修改时间不可读取。 */
export function formatFileTime(milliseconds: number): string {
  if (!milliseconds) {
    return '—'
  }

  return new Date(milliseconds).toLocaleString('zh-CN', { hour12: false })
}
