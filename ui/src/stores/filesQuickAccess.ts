import { ref } from 'vue'
import { defineStore } from 'pinia'
import {
  addFileQuickAccess,
  getFileQuickAccess,
  removeFileQuickAccess,
  type FileQuickAccessItem,
} from '@/api/files'

/**
 * 文件管理快速访问目录。
 * 列表由后端按登录账号保存在 SQLite 中，这里只缓存当前账号的一份快照，
 * 文件管理页左侧栏与目录行菜单共用同一个 store，因此添加/移除后侧栏立即同步。
 * 登录/登出时由 auth store 调用 reset()，避免跨用户串数据。
 */
export const useFilesQuickAccessStore = defineStore('filesQuickAccess', () => {
  const items = ref<FileQuickAccessItem[]>([])
  const loading = ref(false)
  /** 是否已成功加载过；加载失败保持 false，页面据此隐藏侧栏。 */
  const loaded = ref(false)

  let pending: Promise<void> | null = null

  /**
   * 读取快速访问列表。
   * 失败（403 账号不存在、503 文件管理关闭等）时静默保持空列表，不打扰页面；
   * force=false 且已加载时直接复用。
   */
  async function load(force = false): Promise<void> {
    if (loading.value && pending) {
      return pending
    }

    if (loaded.value && !force) {
      return
    }

    loading.value = true
    pending = (async () => {
      try {
        const data = await getFileQuickAccess()
        items.value = data.items
        loaded.value = true
      } catch {
        items.value = []
        loaded.value = false
      } finally {
        loading.value = false
        pending = null
      }
    })()

    return pending
  }

  /** 添加目录；返回后端是否真正新建（false 表示已在列表中）。失败向上抛出由调用方提示。 */
  async function add(path: string): Promise<boolean> {
    const result = await addFileQuickAccess(path)
    if (result.created) {
      if (!items.value.some((item) => item.id === result.item.id)) {
        items.value = [...items.value, result.item]
      }
      loaded.value = true
    }

    return result.created
  }

  /** 移除条目；成功后才从本地快照中删除。 */
  async function remove(id: number): Promise<void> {
    await removeFileQuickAccess(id)
    items.value = items.value.filter((item) => item.id !== id)
  }

  /** 登录/登出时重置，避免上一个用户的列表残留。 */
  function reset(): void {
    items.value = []
    loaded.value = false
    loading.value = false
    pending = null
  }

  return { items, loading, loaded, load, add, remove, reset }
})
