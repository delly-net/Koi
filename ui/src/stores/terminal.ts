import { ref } from 'vue'
import { defineStore } from 'pinia'

/**
 * 其它模块（如文件管理）请求「新建一个终端页签」时携带的目录信息。
 * 终端页签状态由 TerminalView 本地维护，这里只保存一次性请求，消费后立即清空。
 */
export interface TerminalTabRequest {
  /** 请求序号：同一目录连续请求也能被 watcher 感知。 */
  seq: number
  /** 新终端的初始工作目录（绝对路径）；空串表示账号家目录。 */
  cwd: string
  /** 终端就绪后自动执行的命令；空串表示只打开交互式 Shell。 */
  command: string
}

export const useTerminalStore = defineStore('terminal', () => {
  const request = ref<TerminalTabRequest | null>(null)
  let counter = 0

  /**
   * 请求终端页面新建页签并把工作目录切到 cwd；需随后打开工作区「终端」标签。
   * command 非空时，终端就绪后自动执行一次（文件管理「执行」.sh 脚本使用）。
   */
  function requestTab(cwd: string, command = ''): void {
    counter += 1
    request.value = { seq: counter, cwd, command }
  }

  /** 取出并清空未处理的请求：TerminalView 在挂载时与运行中都会调用。 */
  function consume(): TerminalTabRequest | null {
    const value = request.value
    request.value = null
    return value
  }

  /** 登录/登出时清空请求，避免上一个用户的目录请求串到当前用户。 */
  function reset(): void {
    request.value = null
    counter = 0
  }

  return { request, requestTab, consume, reset }
})
