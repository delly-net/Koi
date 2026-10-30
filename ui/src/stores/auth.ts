import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import {
  fetchCurrentUser,
  login as loginRequest,
  logout as logoutRequest,
  type AuthUser,
} from '@/api/auth'
import { useEditorStore } from '@/stores/editor'
import { useFilesClipboardStore } from '@/stores/filesClipboard'
import { useWorkspaceStore } from '@/stores/workspace'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<AuthUser | null>(null)
  const initialized = ref(false)
  const loading = ref(false)

  const isAuthenticated = computed(() => user.value !== null)

  /** 恢复服务端会话；只需成功执行一次。 */
  async function restore(): Promise<void> {
    if (initialized.value) {
      return
    }

    try {
      user.value = await fetchCurrentUser()
    } catch {
      user.value = null
    } finally {
      initialized.value = true
    }
  }

  async function login(userName: string, password: string): Promise<void> {
    loading.value = true
    try {
      user.value = await loginRequest(userName, password)
      initialized.value = true
      // 新会话从空编辑器、空文件剪贴板与首页标签开始，避免上一个用户的状态串到当前用户。
      useEditorStore().reset()
      useFilesClipboardStore().reset()
      useWorkspaceStore().reset()
    } finally {
      loading.value = false
    }
  }

  async function logout(): Promise<void> {
    try {
      await logoutRequest()
    } finally {
      user.value = null
      useEditorStore().reset()
      useFilesClipboardStore().reset()
      // 标签状态随会话结束清空，避免下一个用户看到上一个用户打开的模块。
      useWorkspaceStore().reset()
    }
  }

  return { user, initialized, loading, isAuthenticated, restore, login, logout }
})
