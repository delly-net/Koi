import { ref } from 'vue'
import { defineStore } from 'pinia'

/** 文件剪贴板：记录来源路径与复制/剪切模式，所有文件页签共用。 */
export interface FileClipboard {
  mode: 'copy' | 'cut'
  sources: string[]
}

/**
 * 跨页签共享的文件剪贴板。
 * 在任何文件页签中复制/剪切后，切换到其它页签即可直接粘贴；
 * 状态仅保存在内存中，登出时由 auth store 调用 reset() 清空。
 */
export const useFilesClipboardStore = defineStore('filesClipboard', () => {
  const clipboard = ref<FileClipboard | null>(null)

  /** 设置剪贴板；空来源忽略，避免产生无意义的粘贴状态。 */
  function set(sources: string[], mode: 'copy' | 'cut'): void {
    if (sources.length === 0) {
      return
    }

    clipboard.value = { mode, sources: [...sources] }
  }

  /** 清空剪贴板（剪切粘贴成功后调用）。 */
  function clear(): void {
    clipboard.value = null
  }

  /** 登录/登出时重置，避免上一个用户的内容残留。 */
  function reset(): void {
    clipboard.value = null
  }

  return { clipboard, set, clear, reset }
})
