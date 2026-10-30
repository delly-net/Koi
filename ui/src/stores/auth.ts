import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import {
  fetchCurrentUser,
  login as loginRequest,
  logout as logoutRequest,
  type AuthUser,
} from '@/api/auth'
import { useEditorStore } from '@/stores/editor'

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
      // 新会话从空编辑器开始，避免上一个用户未清理的标签内容串到当前用户。
      useEditorStore().reset()
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
    }
  }

  return { user, initialized, loading, isAuthenticated, restore, login, logout }
})
