import { ref } from 'vue'
import { defineStore } from 'pinia'

/**
 * 文件管理请求打开「图片浏览」时携带的目录信息。
 * 图片浏览页的状态由 ImageView 本地维护，这里只保存一次性请求，消费后立即清空。
 */
export interface ImageBrowserRequest {
  /** 请求序号：同一目录连续请求也能被 watcher 感知。 */
  seq: number
  /** 要加载的目录（绝对路径）。 */
  directory: string
  /** 加载完成后定位并打开的图片路径；空串表示不自动打开。 */
  selectPath: string
}

export const useImageBrowserStore = defineStore('imageBrowser', () => {
  const request = ref<ImageBrowserRequest | null>(null)
  let counter = 0

  /** 请求图片浏览页加载目录，可同时指定要自动打开的图片。 */
  function requestDirectory(directory: string, selectPath = ''): void {
    counter += 1
    request.value = { seq: counter, directory, selectPath }
  }

  /** 取出并清空未处理的请求：ImageView 在挂载时与运行中都会调用。 */
  function consume(): ImageBrowserRequest | null {
    const value = request.value
    request.value = null
    return value
  }

  /** 登录/登出时清空请求，避免上一个用户的目录请求串到当前用户。 */
  function reset(): void {
    request.value = null
    counter = 0
  }

  return { request, requestDirectory, consume, reset }
})
