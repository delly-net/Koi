import { request } from './http'

export interface AuthUser {
  userName: string
  expiresAt: string
}

export function login(userName: string, password: string): Promise<AuthUser> {
  return request<AuthUser>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ userName, password }),
  })
}

export function fetchCurrentUser(): Promise<AuthUser> {
  return request<AuthUser>('/api/auth/me')
}

export function logout(): Promise<void> {
  return request<void>('/api/auth/logout', { method: 'POST' })
}
