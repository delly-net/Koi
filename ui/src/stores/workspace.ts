import { ref } from 'vue'
import { defineStore } from 'pinia'

/** 工作区一级标签：每个标签对应一个模块页面，标签状态仅保存在内存中。 */
export type WorkspaceTabKey = 'home' | 'terminal' | 'files' | 'editor'

/** 标签元信息；首页固定常驻，其余标签按需打开并可关闭。 */
export const WORKSPACE_TAB_TITLES: Record<WorkspaceTabKey, string> = {
  home: '首页',
  terminal: '终端',
  files: '文件管理',
  editor: '文本编辑',
}

/** 首页固定标签，不可关闭。 */
export const HOME_TAB: WorkspaceTabKey = 'home'

/**
 * 工作区标签状态。
 * 标签不随路由变化（纯内存），刷新浏览器后回到首页；
 * 关闭某个标签即卸载对应面板，终端 WS 与 PTY 随之释放。
 */
export const useWorkspaceStore = defineStore('workspace', () => {
  const tabs = ref<WorkspaceTabKey[]>([HOME_TAB])
  const activeKey = ref<WorkspaceTabKey>(HOME_TAB)

  /** 打开标签：已存在则直接激活，避免重复标签。 */
  function open(key: WorkspaceTabKey): void {
    if (!tabs.value.includes(key)) {
      tabs.value.push(key)
    }

    activeKey.value = key
  }

  function activate(key: WorkspaceTabKey): void {
    if (tabs.value.includes(key)) {
      activeKey.value = key
    }
  }

  /** 关闭标签：首页不可关闭；关闭当前标签后回到首页，否则保持当前标签。 */
  function close(key: WorkspaceTabKey): void {
    if (key === HOME_TAB) {
      return
    }

    const index = tabs.value.indexOf(key)
    if (index === -1) {
      return
    }

    tabs.value.splice(index, 1)
    if (activeKey.value === key) {
      activeKey.value = HOME_TAB
    }
  }

  /** 清空标签：登录/登出时调用，避免上一个用户的标签状态残留。 */
  function reset(): void {
    tabs.value = [HOME_TAB]
    activeKey.value = HOME_TAB
  }

  return { tabs, activeKey, open, activate, close, reset }
})
