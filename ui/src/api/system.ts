import { request } from './http'

/** CPU 使用率、核心数与系统负载。 */
export interface CpuStatus {
  usagePercent: number
  cores: number
  load1: number
  load5: number
  load15: number
}

/** 内存与交换分区使用量（字节）。 */
export interface MemoryStatus {
  totalBytes: number
  usedBytes: number
  availableBytes: number
  usagePercent: number
  swapTotalBytes: number
  swapUsedBytes: number
  swapUsagePercent: number
}

/** 单个磁盘挂载点的容量与使用率。 */
export interface DiskStatus {
  mountPoint: string
  fileSystem: string
  totalBytes: number
  usedBytes: number
  freeBytes: number
  usagePercent: number
}

/** 网络累计收发字节与每秒速率。 */
export interface NetworkStatus {
  receivedBytesPerSecond: number
  sentBytesPerSecond: number
  receivedTotalBytes: number
  sentTotalBytes: number
}

/** 一次服务器状态快照（对应 GET /api/system/status）。 */
export interface SystemStatus {
  hostName: string
  kernelVersion: string
  uptimeSeconds: number
  processCount: number
  cpu: CpuStatus
  memory: MemoryStatus
  disks: DiskStatus[]
  network: NetworkStatus
  sampledAt: string
}

/** 读取服务器实时状态；仅 Linux 且已登录时可用。 */
export function getSystemStatus(): Promise<SystemStatus> {
  return request<SystemStatus>('/api/system/status')
}
