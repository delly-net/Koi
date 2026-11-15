import { request } from './http'

/** 当前系统的包管理器状态。 */
export interface PackageStatus {
  available: boolean
  manager: string
  displayName: string
  version: string
  /** 当前登录账号是否为 root。 */
  root: boolean
  /** Koi 服务是否以 root 运行（决定能否安装/卸载/改源）。 */
  canModify: boolean
  /** 是否支持新增/删除/启停软件源。 */
  sourceManagement: boolean
}

export interface PackageSummary {
  name: string
  version: string
  summary: string
}

export interface PackageListResponse {
  manager: string
  total: number
  truncated: boolean
  packages: PackageSummary[]
}

/** 缓存记录的安装状态。 */
export type PackageCacheStatus = 'installed' | 'removed'

/** 已安装软件缓存中的一条记录。 */
export interface PackageCacheEntry {
  id: number
  name: string
  version: string
  /** 简介：首次缓存时预填包管理器描述，之后只由用户修改。 */
  intro: string
  status: PackageCacheStatus
  updatedAt: string
}

/** 缓存分页查询条件；status 为 all / installed / removed。 */
export type PackageCacheStatusFilter = 'all' | PackageCacheStatus

export interface PackageCacheListParams {
  page?: number
  pageSize?: number
  status?: PackageCacheStatusFilter
  query?: string
}

export interface PackageCacheListResponse {
  manager: string
  total: number
  page: number
  pageSize: number
  status: PackageCacheStatusFilter
  lastRefreshedAt: string | null
  packages: PackageCacheEntry[]
}

export interface PackageCacheRefreshResponse {
  manager: string
  total: number
  added: number
  updated: number
  removed: number
  refreshedAt: string
}

export interface PackageSearchResult {
  name: string
  version: string
  summary: string
  installed: boolean
}

export interface PackageSearchResponse {
  manager: string
  results: PackageSearchResult[]
  truncated: boolean
}

export interface PackageUpdate {
  name: string
  currentVersion: string
  newVersion: string
}

export interface PackageUpdatesResponse {
  manager: string
  packages: PackageUpdate[]
}

/** 后台任务动作（固定白名单，与后端一致）。 */
export type PackageAction = 'install' | 'remove' | 'upgrade' | 'upgrade-all' | 'refresh'

export type PackageJobStatus = 'running' | 'succeeded' | 'failed' | 'cancelled'

export interface PackageJobStartResponse {
  jobId: string
  action: PackageAction
  packages: string[]
}

export interface PackageJob {
  id: string
  action: PackageAction
  packages: string[]
  status: PackageJobStatus
  exitCode: number | null
  log: string
  nextOffset: number
  truncated: boolean
  startedAt: string
  endedAt: string | null
}

export interface PackageJobCancelResponse {
  ok: boolean
}

export interface PackageSource {
  id: string
  kind: string
  name: string
  uri: string
  suite: string
  components: string
  enabled: boolean
  file: string
  togglable: boolean
  removable: boolean
  managed: boolean
}

export interface PackageSourceListResponse {
  manager: string
  supported: boolean
  readOnly: boolean
  sources: PackageSource[]
}

export interface PackageSourceAddRequest {
  name: string
  uri: string
  suite?: string
  components?: string
  gpgCheck?: boolean
}

export interface PackageSourceActionResponse {
  ok: boolean
}

/** 应用管理配置（存储于 SQLite 通用配置表，仅 root 可读写）。 */
export interface PackageSettings {
  enabled: boolean
  /** 包管理器：空字符串表示自动检测，其余为 apt/dnf/yum/pacman/zypper。 */
  manager: string
  allowedGroups: string[]
  commandTimeoutSeconds: number
  jobTimeoutSeconds: number
  maxOutputBytes: number
  maxLogBytes: number
  jobRetentionMinutes: number
  maxPackagesPerJob: number
  maxInstalledEntries: number
}

function encode(value: string): string {
  return encodeURIComponent(value)
}

export function getPackageStatus(): Promise<PackageStatus> {
  return request<PackageStatus>('/api/packages/status')
}

/** 读取应用管理设置；非 root 账号会收到 403。 */
export function getPackageSettings(): Promise<PackageSettings> {
  return request<PackageSettings>('/api/packages/settings')
}

/** 保存应用管理设置；成功后立即生效。 */
export function savePackageSettings(payload: PackageSettings): Promise<PackageSettings> {
  return request<PackageSettings>('/api/packages/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

export function listInstalled(query = '', limit?: number): Promise<PackageListResponse> {
  const params = new URLSearchParams()
  if (query.trim().length > 0) {
    params.set('query', query.trim())
  }
  if (limit !== undefined) {
    params.set('limit', String(limit))
  }

  const suffix = params.toString()
  return request<PackageListResponse>(`/api/packages/installed${suffix ? `?${suffix}` : ''}`)
}

/** 分页读取已安装软件缓存（数据来自数据库，不触发包管理器查询）。 */
export function listPackageCache(
  params: PackageCacheListParams = {},
): Promise<PackageCacheListResponse> {
  const search = new URLSearchParams()
  if (params.page !== undefined) {
    search.set('page', String(params.page))
  }
  if (params.pageSize !== undefined) {
    search.set('pageSize', String(params.pageSize))
  }
  if (params.status !== undefined) {
    search.set('status', params.status)
  }
  if (params.query && params.query.trim().length > 0) {
    search.set('query', params.query.trim())
  }

  const suffix = search.toString()
  return request<PackageCacheListResponse>(`/api/packages/cache${suffix ? `?${suffix}` : ''}`)
}

/** 修改缓存记录的简介；刷新缓存不会覆盖该值。 */
export function updatePackageCacheIntro(
  id: number,
  intro: string,
): Promise<PackageCacheEntry> {
  return request<PackageCacheEntry>(`/api/packages/cache/${id}`, {
    method: 'PUT',
    body: JSON.stringify({ intro }),
  })
}

/** 更新缓存：同步查询包管理器并把结果写入数据库。 */
export function refreshPackageCache(): Promise<PackageCacheRefreshResponse> {
  return request<PackageCacheRefreshResponse>('/api/packages/cache/refresh', {
    method: 'POST',
  })
}

export function searchPackages(query: string): Promise<PackageSearchResponse> {
  const params = new URLSearchParams({ q: query.trim() })
  return request<PackageSearchResponse>(`/api/packages/search?${params.toString()}`)
}

export function listUpdates(): Promise<PackageUpdatesResponse> {
  return request<PackageUpdatesResponse>('/api/packages/updates')
}

export function createPackageJob(
  action: PackageAction,
  packages: string[] = [],
): Promise<PackageJobStartResponse> {
  return request<PackageJobStartResponse>('/api/packages/jobs', {
    method: 'POST',
    body: JSON.stringify({ action, packages }),
  })
}

export function getPackageJob(id: string, offset = 0): Promise<PackageJob> {
  const params = new URLSearchParams({ offset: String(offset) })
  return request<PackageJob>(`/api/packages/jobs/${encode(id)}?${params.toString()}`)
}

export function cancelPackageJob(id: string): Promise<PackageJobCancelResponse> {
  return request<PackageJobCancelResponse>(`/api/packages/jobs/${encode(id)}/cancel`, {
    method: 'POST',
  })
}

export function listPackageSources(): Promise<PackageSourceListResponse> {
  return request<PackageSourceListResponse>('/api/packages/sources')
}

export function addPackageSource(payload: PackageSourceAddRequest): Promise<PackageSource> {
  return request<PackageSource>('/api/packages/sources', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export function setPackageSourceEnabled(id: string, enabled: boolean): Promise<PackageSource> {
  return request<PackageSource>(`/api/packages/sources/${encode(id)}/${enabled ? 'enable' : 'disable'}`, {
    method: 'POST',
  })
}

export function removePackageSource(id: string): Promise<PackageSourceActionResponse> {
  return request<PackageSourceActionResponse>(`/api/packages/sources/${encode(id)}`, {
    method: 'DELETE',
  })
}
