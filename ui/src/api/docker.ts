import { request } from './http'

/** Docker 版本、守护进程信息与资源概览。 */
export interface DockerStatus {
  available: boolean
  clientVersion: string
  serverVersion: string
  apiVersion: string
  operatingSystem: string
  architecture: string
  driver: string
  containers: number
  containersRunning: number
  containersPaused: number
  containersStopped: number
  images: number
  memoryTotal: number
  cpus: number
}

/** 容器列表项（对应 docker ps 的 JSON 输出）。 */
export interface DockerContainer {
  id: string
  names: string
  image: string
  command: string
  createdAt: string
  runningFor: string
  status: string
  /** running / exited / paused / created / restarting / dead。 */
  state: string
  ports: string
  labels: string
}

export interface DockerPortBinding {
  containerPort: string
  hostIp: string | null
  hostPort: string | null
}

export interface DockerMountInfo {
  type: string
  source: string
  destination: string
  mode: string
}

/** 容器详情摘要（对应 docker inspect 的精选字段）。 */
export interface DockerContainerDetail {
  id: string
  name: string
  image: string
  status: string
  running: boolean
  paused: boolean
  restarting: boolean
  created: string
  startedAt: string
  finishedAt: string
  exitCode: number
  restartCount: number
  command: string[]
  entrypoint: string[]
  ports: DockerPortBinding[]
  mounts: DockerMountInfo[]
  labels: Record<string, string>
}

/** 容器资源占用快照（对应 docker stats --no-stream）。 */
export interface DockerStatsEntry {
  id: string
  name: string
  cpuPercent: string
  memUsage: string
  memPercent: string
  netIo: string
  blockIo: string
  pids: string
}

/** 镜像列表项（对应 docker images 的 JSON 输出）。 */
export interface DockerImage {
  id: string
  repository: string
  tag: string
  digest: string
  createdAt: string
  size: string
  containers: string
}

export interface DockerActionResponse {
  ok: boolean
  action: string
  id: string
}

export interface DockerLogsResponse {
  id: string
  tail: number
  content: string
  truncated: boolean
}

export interface DockerPullResponse {
  ok: boolean
  reference: string
  output: string
}

export interface DockerPruneResponse {
  ok: boolean
  output: string
}

export interface DockerTagResponse {
  ok: boolean
  source: string
  target: string
}

/** 容器启停类动作（固定白名单，与后端一致）。 */
export type DockerAction = 'start' | 'stop' | 'restart' | 'pause' | 'unpause'

function encodeId(id: string): string {
  return encodeURIComponent(id)
}

export function getDockerStatus(): Promise<DockerStatus> {
  return request<DockerStatus>('/api/docker/status')
}

export function listContainers(): Promise<DockerContainer[]> {
  return request<DockerContainer[]>('/api/docker/containers')
}

export function inspectContainer(id: string): Promise<DockerContainerDetail> {
  return request<DockerContainerDetail>(`/api/docker/containers/${encodeId(id)}`)
}

export function containerAction(id: string, action: DockerAction): Promise<DockerActionResponse> {
  return request<DockerActionResponse>(`/api/docker/containers/${encodeId(id)}/${action}`, {
    method: 'POST',
  })
}

export function removeContainer(
  id: string,
  force = false,
  volumes = false,
): Promise<DockerActionResponse> {
  const query = new URLSearchParams()
  if (force) {
    query.set('force', 'true')
  }
  if (volumes) {
    query.set('volumes', 'true')
  }

  const suffix = query.toString() ? `?${query.toString()}` : ''
  return request<DockerActionResponse>(`/api/docker/containers/${encodeId(id)}${suffix}`, {
    method: 'DELETE',
  })
}

export function getContainerLogs(
  id: string,
  tail = 500,
  timestamps = true,
): Promise<DockerLogsResponse> {
  const query = new URLSearchParams({ tail: String(tail) })
  if (timestamps) {
    query.set('timestamps', 'true')
  }

  return request<DockerLogsResponse>(
    `/api/docker/containers/${encodeId(id)}/logs?${query.toString()}`,
  )
}

export function getDockerStats(): Promise<DockerStatsEntry[]> {
  return request<DockerStatsEntry[]>('/api/docker/stats')
}

export function listImages(): Promise<DockerImage[]> {
  return request<DockerImage[]>('/api/docker/images')
}

export function pullImage(reference: string): Promise<DockerPullResponse> {
  return request<DockerPullResponse>('/api/docker/images/pull', {
    method: 'POST',
    body: JSON.stringify({ reference }),
  })
}

export function tagImage(source: string, target: string): Promise<DockerTagResponse> {
  return request<DockerTagResponse>('/api/docker/images/tag', {
    method: 'POST',
    body: JSON.stringify({ source, target }),
  })
}

export function removeImage(id: string, force = false): Promise<DockerActionResponse> {
  const suffix = force ? '?force=true' : ''
  return request<DockerActionResponse>(`/api/docker/images/${encodeId(id)}${suffix}`, {
    method: 'DELETE',
  })
}

export function pruneImages(): Promise<DockerPruneResponse> {
  return request<DockerPruneResponse>('/api/docker/images/prune', { method: 'POST' })
}
