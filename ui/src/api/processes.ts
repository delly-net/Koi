import { request } from './http'

/** 进程列表项；CPU 占比以单核为 100%，多线程进程可超过 100。 */
export interface ProcessSummary {
  pid: number
  parentPid: number
  name: string
  user: string
  uid: number
  /** /proc/[pid]/stat 的状态字母，如 R / S / D / Z / T。 */
  state: string
  cpuPercent: number
  memoryPercent: number
  rssBytes: number
  virtualBytes: number
  threads: number
  runtimeSeconds: number
  commandLine: string
}

/** 一次进程列表快照。 */
export interface ProcessListResponse {
  processes: ProcessSummary[]
  total: number
  /** 超出最大进程数被截断。 */
  truncated: boolean
  sampledAt: string
  /** 前端自动刷新间隔（秒），由服务端配置决定。 */
  refreshIntervalSeconds: number
}

/** 进程详情（列表字段 + 可执行文件、工作目录、IO 与文件描述符）。 */
export interface ProcessDetail {
  pid: number
  parentPid: number
  name: string
  user: string
  uid: number
  state: string
  cpuPercent: number
  memoryPercent: number
  rssBytes: number
  virtualBytes: number
  threads: number
  runtimeSeconds: number
  commandLine: string
  executablePath: string
  workingDirectory: string
  readBytes: number | null
  writeBytes: number | null
  openFileCount: number | null
  sampledAt: string
}

export interface ProcessTerminateResponse {
  ok: boolean
  pid: number
  signal: string
  exited: boolean
  waitedMilliseconds: number
}

/** 进程管理设置（仅 root 可读写，保存在 SQLite 通用配置表）。 */
export interface ProcessSettings {
  enabled: boolean
  allowedGroups: string[]
  maxProcesses: number
  maxCommandLineLength: number
  sampleWindowMilliseconds: number
  terminateTimeoutSeconds: number
  refreshIntervalSeconds: number
}

export function listProcesses(): Promise<ProcessListResponse> {
  return request<ProcessListResponse>('/api/processes')
}

export function getProcess(pid: number): Promise<ProcessDetail> {
  return request<ProcessDetail>(`/api/processes/${pid}`)
}

/** 结束进程：force=false 发送 SIGTERM，force=true 发送 SIGKILL。 */
export function terminateProcess(pid: number, force = false): Promise<ProcessTerminateResponse> {
  return request<ProcessTerminateResponse>(`/api/processes/${pid}/terminate?force=${force}`, {
    method: 'POST',
  })
}

/** 读取进程管理设置；仅 root 成功，非 root 返回 403。 */
export function getProcessSettings(): Promise<ProcessSettings> {
  return request<ProcessSettings>('/api/processes/settings')
}

/** 保存进程管理设置；成功后立即生效。 */
export function saveProcessSettings(payload: ProcessSettings): Promise<ProcessSettings> {
  return request<ProcessSettings>('/api/processes/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}
