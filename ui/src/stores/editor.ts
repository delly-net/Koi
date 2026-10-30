import { ref } from 'vue'
import { defineStore } from 'pinia'
import { readTextFile } from '@/api/files'
import { ApiError } from '@/api/http'

/** 一个编辑标签的状态；内容保存在 store 中，路由切换不会丢失未保存的修改。 */
export interface EditorTab {
  path: string
  name: string
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
  const activePath = ref('')

  function find(path: string): EditorTab | undefined {
    return tabs.value.find((tab) => tab.path === path)
  }

  /**
   * 打开文件标签：已打开则直接激活，否则先占位再异步读取。
   * 读取失败记录在标签的 error 上，由页面展示并提供重试，不向调用方抛异常。
   */
  async function openFile(path: string): Promise<void> {
    const existing = find(path)
    if (existing) {
      activePath.value = path
      return
    }

    tabs.value.push({
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
    activePath.value = path

    // push 后取回响应式代理，直接修改原对象不会触发视图更新。
    const tab = find(path)
    if (!tab) {
      return
    }

    try {
      const data = await readTextFile(path)
      tab.content = data.content
      tab.savedContent = data.content
      tab.modifiedAt = data.modifiedAt
      tab.size = data.size
      tab.hasBom = data.hasBom
    } catch (error) {
      tab.error = errorText(error)
    } finally {
      tab.loading = false
    }
  }

  /** 关闭标签；删除当前标签后优先激活后一个，否则激活前一个。 */
  function closeTab(path: string): void {
    const index = tabs.value.findIndex((tab) => tab.path === path)
    if (index === -1) {
      return
    }

    tabs.value.splice(index, 1)
    if (activePath.value !== path) {
      return
    }

    const next = tabs.value[index] ?? tabs.value[index - 1] ?? null
    activePath.value = next?.path ?? ''
  }

  /** 清空所有标签：登录/登出时调用，避免上一个用户的文件内容留在共享浏览器会话中。 */
  function reset(): void {
    tabs.value = []
    activePath.value = ''
  }

  return { tabs, activePath, openFile, closeTab, reset }
})
