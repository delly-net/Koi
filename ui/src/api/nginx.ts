import { request } from './http'

/** Nginx 管理配置（仅 root 可读写，保存在 SQLite 通用配置表）。 */
export interface NginxSettings {
  enabled: boolean
  /** nginx 路径；空字符串表示自动探测。 */
  nginxPath: string
  /** 可在线查看与编辑的配置目录（仅顶层 *.conf）。 */
  confDirectory: string
  /** nginx 主配置路径；空字符串表示使用 nginx 默认配置。 */
  mainConfigPath: string
  allowedGroups: string[]
  commandTimeoutSeconds: number
  maxOutputBytes: number
  maxFileBytes: number
  maxListEntries: number
}

export interface NginxStatus {
  enabled: boolean
  available: boolean
  /** nginx -v 的输出，例如 nginx version: nginx/1.26.1。 */
  version: string
  confDirectory: string
}

export interface NginxConfigEntry {
  name: string
  size: number
  modifiedAt: number
}

export interface NginxConfigList {
  directory: string
  files: NginxConfigEntry[]
  truncated: boolean
}

export interface NginxConfigContent {
  name: string
  path: string
  content: string
  modifiedAt: number
  size: number
  hasBom: boolean
}

export interface NginxConfigSaveResponse {
  ok: boolean
  name: string
  path: string
  modifiedAt: number
  size: number
}

export interface NginxCommandResponse {
  success: boolean
  output: string
}

function encodeName(name: string): string {
  return encodeURIComponent(name)
}

/** 读取 Nginx 管理设置；仅 root 成功，非 root 返回 403。 */
export function getNginxSettings(): Promise<NginxSettings> {
  return request<NginxSettings>('/api/nginx/settings')
}

/** 保存 Nginx 管理设置；成功后立即生效。 */
export function saveNginxSettings(payload: NginxSettings): Promise<NginxSettings> {
  return request<NginxSettings>('/api/nginx/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

export function getNginxStatus(): Promise<NginxStatus> {
  return request<NginxStatus>('/api/nginx/status')
}

export function listNginxConfigs(): Promise<NginxConfigList> {
  return request<NginxConfigList>('/api/nginx/configs')
}

export function readNginxConfig(name: string): Promise<NginxConfigContent> {
  return request<NginxConfigContent>(`/api/nginx/configs/${encodeName(name)}`)
}

/**
 * 保存 nginx 配置文件。
 * `expectedModifiedAt` 为读取/上次保存时的修改时间，服务端不一致时返回 409；
 * 传 0 表示强制覆盖（用户确认后）。
 */
export function saveNginxConfig(
  name: string,
  content: string,
  expectedModifiedAt: number,
  hasBom: boolean,
): Promise<NginxConfigSaveResponse> {
  return request<NginxConfigSaveResponse>(`/api/nginx/configs/${encodeName(name)}`, {
    method: 'PUT',
    body: JSON.stringify({ content, expectedModifiedAt, hasBom }),
  })
}

/** 执行 nginx -t；语法错误时仍返回 200，success=false 并携带命令输出。 */
export function testNginxConfig(): Promise<NginxCommandResponse> {
  return request<NginxCommandResponse>('/api/nginx/test', { method: 'POST' })
}

/** 执行 nginx -s reload 热加载配置。 */
export function reloadNginxConfig(): Promise<NginxCommandResponse> {
  return request<NginxCommandResponse>('/api/nginx/reload', { method: 'POST' })
}
