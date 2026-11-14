import { request } from './http'

/** 服务列表项：合并 systemctl list-units 与 list-unit-files 的状态。 */
export interface ServiceSummary {
  name: string
  description: string
  loadState: string
  activeState: string
  subState: string
  /** enabled / disabled / static / masked / 空串（运行时生成单元）。 */
  unitFileState: string
}

/** 服务详情（systemctl show 的精选字段）。 */
export interface ServiceDetail {
  name: string
  description: string
  loadState: string
  activeState: string
  subState: string
  unitFileState: string
  fragmentPath: string
  mainPid: number | null
  execMainPid: number | null
  activeEnterTimestamp: string
  memoryCurrent: number | null
  tasksCurrent: number | null
  restartCount: number
  result: string
  execStart: string
  canReload: string
  needDaemonReload: string
}

export interface ServiceActionResponse {
  ok: boolean
  action: string
  name: string
}

export interface ServiceLogsResponse {
  name: string
  tail: number
  content: string
  truncated: boolean
}

/** 服务生命周期动作（固定白名单，与后端一致）。 */
export type ServiceAction = 'start' | 'stop' | 'restart' | 'reload' | 'enable' | 'disable'

function encodeName(name: string): string {
  return encodeURIComponent(name)
}

export function listServices(): Promise<ServiceSummary[]> {
  return request<ServiceSummary[]>('/api/services')
}

export function getService(name: string): Promise<ServiceDetail> {
  return request<ServiceDetail>(`/api/services/${encodeName(name)}`)
}

export function serviceAction(name: string, action: ServiceAction): Promise<ServiceActionResponse> {
  return request<ServiceActionResponse>(`/api/services/${encodeName(name)}/${action}`, {
    method: 'POST',
  })
}

export function getServiceLogs(name: string, tail = 500): Promise<ServiceLogsResponse> {
  const query = new URLSearchParams({ tail: String(tail) })
  return request<ServiceLogsResponse>(`/api/services/${encodeName(name)}/logs?${query.toString()}`)
}
