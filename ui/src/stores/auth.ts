import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import {
  fetchCurrentUser,
  login as loginRequest,
  logout as logoutRequest,
  type AuthUser,
} from '@/api/auth'

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
    } finally {
      loading.value = false
    }
  }

  async function logout(): Promise<void> {
    try {
      await logoutRequest()
    } finally {
      user.value = null
    }
  }

  return { user, initialized, loading, isAuthenticated, restore, login, logout }
})
