import { ref } from 'vue'
import { defineStore } from 'pinia'
import { readTextFile, saveTextFile } from '@/api/files'
import { readNginxConfig, saveNginxConfig } from '@/api/nginx'
import { ApiError } from '@/api/http'

/** 编辑标签的数据来源：普通文件走文件 API，Nginx 配置走 Nginx 管理 API。 */
export type EditorTabSource = 'files' | 'nginx'

/** 一个编辑标签的状态；内容保存在 store 中，路由切换不会丢失未保存的修改。 */
export interface EditorTab {
  /** 标签唯一键：files:<路径> 或 nginx:<文件名>。 */
  key: string
  source: EditorTabSource
  /** 展示路径；Nginx 配置为 conf.d 中的完整路径。 */
  path: string
  name: string
  /** Nginx 配置文件名；source 为 nginx 时使用。 */
  nginxName?: string
  content: string
  /** 最近一次读取/保存成功时的内容，用于判断是否有未保存修改。 */
  savedContent: string
  /** 最近一次读取/保存成功时的修改时间（Unix 毫秒），保存时用于冲突检测。 */
  modifiedAt: number
  size: number
  hasBom: boolean
  loading: boolean
  saving: boolean
  error: string
}

/** 读取结果：文件 API 与 Nginx API 的公共字段。 */
export interface EditorContent {
  content: string
  modifiedAt: number
  size: number
  hasBom: boolean
  path?: string
}

/** 保存结果：两种来源都只关心修改时间与大小。 */
export interface EditorSaveResult {
  modifiedAt: number
  size: number
}

function fileName(path: string): string {
  return path.split('/').pop() || path
}

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    return error.detail ? `${error.message}：${error.detail}` : error.message
  }

  return error instanceof Error ? error.message : '读取文件失败'
}

export const useEditorStore = defineStore('editor', () => {
  const tabs = ref<EditorTab[]>([])
  const activeKey = ref('')

  function find(key: string): EditorTab | undefined {
    return tabs.value.find((tab) => tab.key === key)
  }

  /** 按标签来源读取内容；编辑器页面与标签自身的加载都走这里。 */
  async function readTabContent(tab: EditorTab): Promise<EditorContent> {
    if (tab.source === 'nginx') {
      if (!tab.nginxName) {
        throw new Error('缺少 Nginx 配置文件名')
      }

      return readNginxConfig(tab.nginxName)
    }

    return readTextFile(tab.path)
  }

  /** 按标签来源保存内容；expectedModifiedAt=0 表示强制覆盖。 */
  async function saveTabContent(tab: EditorTab, expectedModifiedAt: number): Promise<EditorSaveResult> {
    if (tab.source === 'nginx') {
      if (!tab.nginxName) {
        throw new Error('缺少 Nginx 配置文件名')
      }

      const result = await saveNginxConfig(
        tab.nginxName,
        tab.content,
        expectedModifiedAt,
        tab.hasBom,
      )
      return { modifiedAt: result.modifiedAt, size: result.size }
    }

    return saveTextFile(tab.path, tab.content, expectedModifiedAt, tab.hasBom)
  }

  /** 读取成功时把内容写回标签；读取失败记录在标签的 error 上，由页面展示并提供重试。 */
  async function load(tab: EditorTab): Promise<void> {
    tab.loading = true
    tab.error = ''
    try {
      const data = await readTabContent(tab)
      tab.content = data.content
      tab.savedContent = data.content
      tab.modifiedAt = data.modifiedAt
      tab.size = data.size
      tab.hasBom = data.hasBom
      if (data.path) {
        tab.path = data.path
      }
    } catch (error) {
      tab.error = errorText(error)
    } finally {
      tab.loading = false
    }
  }

  /** 创建并激活标签，返回 push 后的响应式代理。 */
  function addTab(tab: EditorTab): EditorTab | undefined {
    tabs.value.push(tab)
    activeKey.value = tab.key
    // push 后取回响应式代理，直接修改原对象不会触发视图更新。
    return find(tab.key)
  }

  /**
   * 打开普通文件标签：已打开则直接激活，否则先占位再异步读取。
   * 读取失败记录在标签的 error 上，由页面展示并提供重试，不向调用方抛异常。
   */
  async function openFile(path: string): Promise<void> {
    const key = `files:${path}`
    const existing = find(key)
    if (existing) {
      activeKey.value = key
      return
    }

    const tab = addTab({
      key,
      source: 'files',
      path,
      name: fileName(path),
      content: '',
      savedContent: '',
      modifiedAt: 0,
      size: 0,
      hasBom: false,
      loading: true,
      saving: false,
      error: '',
    })
    if (tab) {
      await load(tab)
    }
  }

  /**
   * 打开 Nginx 配置文件标签：经 /api/nginx/configs 读写，
   * 使属于允许组的普通账号也能在 Nginx 授权下编辑 root 所有的配置文件。
   */
  async function openNginxConfig(name: string, path: string): Promise<void> {
    const key = `nginx:${name}`
    const existing = find(key)
    if (existing) {
      activeKey.value = key
      return
    }

    const tab = addTab({
      key,
      source: 'nginx',
      path,
      name: fileName(path),
      nginxName: name,
      content: '',
      savedContent: '',
      modifiedAt: 0,
      size: 0,
      hasBom: false,
      loading: true,
      saving: false,
      error: '',
    })
    if (tab) {
      await load(tab)
    }
  }

  /** 关闭标签；删除当前标签后优先激活后一个，否则激活前一个。 */
  function closeTab(key: string): void {
    const index = tabs.value.findIndex((tab) => tab.key === key)
    if (index === -1) {
      return
    }

    tabs.value.splice(index, 1)
    if (activeKey.value !== key) {
      return
    }

    const next = tabs.value[index] ?? tabs.value[index - 1] ?? null
    activeKey.value = next?.key ?? ''
  }

  /** 清空所有标签：登录/登出时调用，避免上一个用户的内容留在共享浏览器会话中。 */
  function reset(): void {
    tabs.value = []
    activeKey.value = ''
  }

  return {
    tabs,
    activeKey,
    readTabContent,
    saveTabContent,
    openFile,
    openNginxConfig,
    closeTab,
    reset,
  }
})
