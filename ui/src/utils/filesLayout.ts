/**
 * 「文件管理」快速访问侧栏宽度的本地记忆。
 *
 * 宽度只是界面偏好，保存在浏览器 localStorage 中，按登录账号区分：
 * 同一浏览器切换账号后各自恢复自己的宽度，不会串用。
 * localStorage 可能被隐私模式、企业策略或配额限制禁用，所有读写都按
 * best-effort 处理——失败时仅告警，拖拽与页面功能不受影响。
 */

/** 默认宽度，与引入可调宽度之前的固定值一致。 */
export const QUICK_ACCESS_DEFAULT_WIDTH = 200
/** 最小宽度，保证侧栏标题与设置按钮可用。 */
export const QUICK_ACCESS_MIN_WIDTH = 160
/** 最大宽度，避免侧栏挤占文件列表。 */
export const QUICK_ACCESS_MAX_WIDTH = 480

/** 上下分栏时快速访问的默认高度。 */
export const QUICK_ACCESS_DEFAULT_HEIGHT = 300
/** 上部分最小高度，保证标题、设置按钮与若干条目可见。 */
export const QUICK_ACCESS_MIN_HEIGHT = 120

const KEY_PREFIX = 'koi.files.quickAccessWidth.'
/** 上部分高度的独立存储键前缀；与宽度分开记忆。 */
const HEIGHT_KEY_PREFIX = 'koi.files.quickAccessHeight.'

/** 访问 localStorage；隐私模式或禁用存储时浏览器可能直接抛异常。 */
function getStorage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

/** 按账号生成存储键；账号名编码后避免特殊字符破坏键结构。 */
export function quickAccessWidthKey(userName: string): string {
  return `${KEY_PREFIX}${encodeURIComponent(userName)}`
}

/**
 * 把宽度夹在允许区间内并取整。
 * maxWidth 用于按容器可用宽度进一步限制（不小于最小宽度）。
 */
export function clampQuickAccessWidth(
  width: number,
  maxWidth: number = QUICK_ACCESS_MAX_WIDTH,
): number {
  const upper = Math.max(
    QUICK_ACCESS_MIN_WIDTH,
    Math.min(QUICK_ACCESS_MAX_WIDTH, Math.floor(maxWidth)),
  )
  return Math.min(upper, Math.max(QUICK_ACCESS_MIN_WIDTH, Math.round(width)))
}

/** 读取账号已保存的宽度；无记录、非法值或存储不可用时返回 null。 */
export function loadQuickAccessWidth(
  userName: string,
  maxWidth: number = QUICK_ACCESS_MAX_WIDTH,
): number | null {
  if (!userName) {
    return null
  }

  const storage = getStorage()
  if (!storage) {
    return null
  }

  try {
    const raw = storage.getItem(quickAccessWidthKey(userName))
    if (raw === null) {
      return null
    }

    const value = Number(raw)
    if (!Number.isFinite(value)) {
      return null
    }

    return clampQuickAccessWidth(value, maxWidth)
  } catch (error) {
    console.warn('读取快速访问栏宽度失败', error)
    return null
  }
}

/** 保存账号的宽度；存储不可用或写入失败只告警，不阻塞界面。 */
export function saveQuickAccessWidth(userName: string, width: number): void {
  if (!userName) {
    return
  }

  const storage = getStorage()
  if (!storage) {
    return
  }

  try {
    storage.setItem(quickAccessWidthKey(userName), String(clampQuickAccessWidth(width)))
  } catch (error) {
    console.warn('保存快速访问栏宽度失败', error)
  }
}

/** 按账号生成上部分高度的存储键。 */
export function quickAccessHeightKey(userName: string): string {
  return `${HEIGHT_KEY_PREFIX}${encodeURIComponent(userName)}`
}

/**
 * 把上部分高度夹在允许区间内并取整。
 * maxHeight 由容器实时高度决定（需为下方目录树保留空间），不小于最小高度。
 */
export function clampQuickAccessHeight(
  height: number,
  maxHeight: number = Number.MAX_SAFE_INTEGER,
): number {
  const upper = Math.max(QUICK_ACCESS_MIN_HEIGHT, Math.floor(maxHeight))
  return Math.min(upper, Math.max(QUICK_ACCESS_MIN_HEIGHT, Math.round(height)))
}

/** 读取账号已保存的上部分高度；无记录、非法值或存储不可用时返回 null。 */
export function loadQuickAccessHeight(
  userName: string,
  maxHeight: number = Number.MAX_SAFE_INTEGER,
): number | null {
  if (!userName) {
    return null
  }

  const storage = getStorage()
  if (!storage) {
    return null
  }

  try {
    const raw = storage.getItem(quickAccessHeightKey(userName))
    if (raw === null) {
      return null
    }

    const value = Number(raw)
    if (!Number.isFinite(value)) {
      return null
    }

    return clampQuickAccessHeight(value, maxHeight)
  } catch (error) {
    console.warn('读取快速访问栏高度失败', error)
    return null
  }
}

/** 保存账号的上部分高度；存储不可用或写入失败只告警，不阻塞界面。 */
export function saveQuickAccessHeight(userName: string, height: number): void {
  if (!userName) {
    return
  }

  const storage = getStorage()
  if (!storage) {
    return
  }

  try {
    storage.setItem(quickAccessHeightKey(userName), String(clampQuickAccessHeight(height)))
  } catch (error) {
    console.warn('保存快速访问栏高度失败', error)
  }
}
