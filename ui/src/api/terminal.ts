import { request } from './http'
import { buildWebSocketUrl } from '@/config/appConfig'

/** 服务端 → 客户端：终端已就绪。 */
export interface TerminalReadyMessage {
  type: 'ready'
  sessionId: string
  userName: string
  pid: number
  cols: number
  rows: number
}

/** 服务端 → 客户端：终端输出（已按 UTF-8 解码）。 */
export interface TerminalOutputMessage {
  type: 'output'
  data: string
}

/** 服务端 → 客户端：子进程已退出。 */
export interface TerminalExitMessage {
  type: 'exit'
  code: number
}

/** 服务端 → 客户端：会话级错误。 */
export interface TerminalErrorMessage {
  type: 'error'
  message: string
}

/** 服务端 → 客户端：心跳应答。 */
export interface TerminalPongMessage {
  type: 'pong'
}

export type TerminalServerMessage =
  | TerminalReadyMessage
  | TerminalOutputMessage
  | TerminalExitMessage
  | TerminalErrorMessage
  | TerminalPongMessage

/** 客户端 → 服务端控制帧。 */
export type TerminalClientMessage =
  | { type: 'input'; data: string }
  | { type: 'resize'; cols: number; rows: number }
  | { type: 'ping' }

/**
 * 打开终端 WebSocket。
 * 地址经 buildWebSocketUrl 拼接：同源时浏览器自动携带会话 Cookie，跨域部署需后端放行来源。
 */
export function openTerminalSocket(): WebSocket {
  return new WebSocket(buildWebSocketUrl('/api/terminal/ws'))
}

/** 终端配置（存储于 SQLite 通用配置表，仅 root 可读写）。 */
export interface TerminalSettings {
  enabled: boolean
  /** 登录 shell 绝对路径；空字符串表示使用系统账号的登录 shell。 */
  shell: string
  shellArguments: string[]
  maxSessionsPerUser: number
  maxSessionsTotal: number
  maxInputBytes: number
  defaultCols: number
  defaultRows: number
  closeTimeoutSeconds: number
}

/** 读取终端配置；非 root 账号会收到 403。 */
export function getTerminalSettings(): Promise<TerminalSettings> {
  return request<TerminalSettings>('/api/terminal/settings')
}

/** 保存终端配置；成功后新会话立即使用新值。 */
export function saveTerminalSettings(payload: TerminalSettings): Promise<TerminalSettings> {
  return request<TerminalSettings>('/api/terminal/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}
