import { ref } from 'vue'
import { defineStore } from 'pinia'
import {
  QUICK_ACCESS_DEFAULT_HEIGHT,
  QUICK_ACCESS_DEFAULT_WIDTH,
  clampQuickAccessHeight,
  clampQuickAccessWidth,
  loadQuickAccessHeight,
  loadQuickAccessWidth,
  saveQuickAccessHeight,
  saveQuickAccessWidth,
} from '@/utils/filesLayout'

/**
 * 文件页签侧栏尺寸（账号级共享）。
 *
 * 每个文件页签都渲染自己的快速访问侧栏，但宽度/高度是账号级偏好：
 * 在任一页签拖动后所有已打开页签同步变化，并按登录账号保存在 localStorage。
 * 底层读写仍由 utils/filesLayout.ts 完成（best-effort，失败仅告警）。
 */
export const useFilesSidebarLayoutStore = defineStore('filesSidebarLayout', () => {
  const width = ref(QUICK_ACCESS_DEFAULT_WIDTH)
  const height = ref(QUICK_ACCESS_DEFAULT_HEIGHT)
  /** 当前尺寸所属账号，用于账号变化时重新读取本地记忆。 */
  const account = ref('')
  let restored = false

  /**
   * 首次挂载或账号变化时恢复该账号保存的尺寸。
   * 同一账号再次调用时只按当前容器上限夹取，不覆盖用户刚拖出的值。
   */
  function restore(userName: string, maxWidth: number, maxHeight: number): void {
    if (!userName) {
      return
    }

    if (restored && account.value === userName) {
      width.value = clampQuickAccessWidth(width.value, maxWidth)
      height.value = clampQuickAccessHeight(height.value, maxHeight)
      return
    }

    account.value = userName
    restored = true
    width.value = loadQuickAccessWidth(userName, maxWidth) ?? QUICK_ACCESS_DEFAULT_WIDTH
    height.value = clampQuickAccessHeight(
      loadQuickAccessHeight(userName, maxHeight) ?? QUICK_ACCESS_DEFAULT_HEIGHT,
      maxHeight,
    )
  }

  function setWidth(value: number, maxWidth: number): void {
    width.value = clampQuickAccessWidth(value, maxWidth)
  }

  function setHeight(value: number, maxHeight: number): void {
    height.value = clampQuickAccessHeight(value, maxHeight)
  }

  /** 拖拽结束、双击或键盘调整后落盘，避免 pointermove 高频写入。 */
  function persistWidth(): void {
    if (account.value) {
      saveQuickAccessWidth(account.value, width.value)
    }
  }

  function persistHeight(): void {
    if (account.value) {
      saveQuickAccessHeight(account.value, height.value)
    }
  }

  /** 登录/登出时重置，避免上一个账号的尺寸残留。 */
  function reset(): void {
    width.value = QUICK_ACCESS_DEFAULT_WIDTH
    height.value = QUICK_ACCESS_DEFAULT_HEIGHT
    account.value = ''
    restored = false
  }

  return {
    width,
    height,
    restore,
    setWidth,
    setHeight,
    persistWidth,
    persistHeight,
    reset,
  }
})
