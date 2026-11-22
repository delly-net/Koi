import { buildApiUrl } from '@/config/appConfig'
import { ApiError, request } from './http'

/** 目录条目（服务端已按「目录优先、名称升序」排序）。 */
export interface FileEntry {
  name: string
  path: string
  isDirectory: boolean
  isSymlink: boolean
  size: number
  /** 修改时间（Unix 毫秒），0 表示不可读取。 */
  modifiedAt: number
  /** Unix 权限位文本，如 drwxr-xr-x。 */
  mode: string
}

export interface FileListResponse {
  path: string
  parent: string | null
  entries: FileEntry[]
  truncated: boolean
}

export interface FileRootsResponse {
  home: string
  roots: string[]
}

/** 快速访问目录；按登录账号隔离，列表只是导航快捷方式，不构成权限授予。 */
export interface FileQuickAccessItem {
  id: number
  path: string
  /** 显示名称：路径最后一段，根目录为 /。 */
  name: string
}

export interface FileQuickAccessListResponse {
  items: FileQuickAccessItem[]
}

export interface FileQuickAccessAddResponse {
  ok: boolean
  /** false 表示该目录已在列表中，未重复添加。 */
  created: boolean
  item: FileQuickAccessItem
}

export interface FileMutationResponse {
  ok: boolean
  /** 仅粘贴被用户取消时为 true（HTTP 200）；已复制的条目会保留。 */
  cancelled?: boolean
}

/** 递归图片扫描结果条目（相对路径用于展示与排序）。 */
export interface FileImageEntry {
  name: string
  path: string
  relativePath: string
  size: number
  /** 修改时间（Unix 毫秒）；同时作为缩略图/原图的缓存版本号。 */
  modifiedAt: number
}

export interface FileImageListResponse {
  path: string
  images: FileImageEntry[]
  /** 达到数量、深度或访问条目上限时为 true。 */
  truncated: boolean
}

/** 复制/移动的进度阶段：统计 → 复制/移动 → 完成/取消/失败。 */
export type FileTransferPhase = 'scanning' | 'transferring' | 'done' | 'cancelled' | 'failed'

/** 粘贴进度快照；由主轮询接口返回，仅发起操作的账号可读取。 */
export interface FileTransferProgress {
  operationId: string
  mode: 'copy' | 'move'
  phase: FileTransferPhase
  bytesDone: number
  bytesTotal: number
  entriesDone: number
  entriesTotal: number
  currentName: string | null
  cancelled: boolean
  completed: boolean
  failed: boolean
  message: string | null
}

export interface FileTransferCancelResponse {
  ok: boolean
  /** false 表示操作已结束，取消未生效。 */
  cancelled: boolean
  completed: boolean
}

/** 文件管理配置（存储于 SQLite 通用配置表，仅 root 可读写）。 */
export interface FileSettings {
  enabled: boolean
  /** 允许访问的根目录绝对路径；默认 ["/"] 表示整个文件系统。 */
  allowedRoots: string[]
  maxUploadBytes: number
  maxListEntries: number
  maxTextFileBytes: number
  helperTimeoutSeconds: number
  listTimeoutSeconds: number
  transferTimeoutSeconds: number
  streamIdleTimeoutSeconds: number
  /** 上传/下载总时长上限（秒），0 表示不限制。 */
  streamMaxSeconds: number
}

/** 读取文本文件的结果（服务端仅接受 UTF-8）。 */
export interface TextFileResponse {
  path: string
  content: string
  /** 修改时间（Unix 毫秒），保存时用于冲突检测。 */
  modifiedAt: number
  /** 文件字节数（含 BOM）。 */
  size: number
  /** 原文件是否带 UTF-8 BOM，保存时原样保留。 */
  hasBom: boolean
}

export interface TextFileSaveResponse {
  ok: boolean
  modifiedAt: number
  size: number
}

interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
}

function encodePath(path: string): string {
  return encodeURIComponent(path)
}

/** 读取家目录与允许访问的根目录。 */
export function getFileRoots(): Promise<FileRootsResponse> {
  return request<FileRootsResponse>('/api/files/roots')
}

/** 读取文件管理设置；非 root 账号会收到 403。 */
export function getFileSettings(): Promise<FileSettings> {
  return request<FileSettings>('/api/files/settings')
}

/** 保存文件管理设置；成功后立即生效。 */
export function saveFileSettings(payload: FileSettings): Promise<FileSettings> {
  return request<FileSettings>('/api/files/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

/** 读取当前账号的快速访问目录列表。 */
export function getFileQuickAccess(): Promise<FileQuickAccessListResponse> {
  return request<FileQuickAccessListResponse>('/api/files/quick-access')
}

/** 把目录加入当前账号的快速访问列表；重复添加幂等返回既有条目。 */
export function addFileQuickAccess(path: string): Promise<FileQuickAccessAddResponse> {
  return request<FileQuickAccessAddResponse>('/api/files/quick-access', {
    method: 'POST',
    body: JSON.stringify({ path }),
  })
}

/** 移除当前账号的一条快速访问记录。 */
export function removeFileQuickAccess(id: number): Promise<FileMutationResponse> {
  return request<FileMutationResponse>(`/api/files/quick-access/${id}`, {
    method: 'DELETE',
  })
}

/** 列出目录；不传路径时后端返回登录用户的家目录。 */
export function listFiles(path?: string): Promise<FileListResponse> {
  const query = path ? `?path=${encodePath(path)}` : ''
  return request<FileListResponse>(`/api/files/list${query}`)
}

/** 递归列出目录（含子目录）中的图片；不传路径时后端返回登录用户的家目录。 */
export function listImages(path?: string): Promise<FileImageListResponse> {
  const query = path ? `?path=${encodePath(path)}` : ''
  return request<FileImageListResponse>(`/api/files/images${query}`)
}

/** 原图地址：浏览器/查看器直接加载，会话 Cookie 随同源请求携带。 */
export function buildImageUrl(path: string, modifiedAt: number): string {
  const version = Number.isFinite(modifiedAt) ? Math.max(0, Math.trunc(modifiedAt)) : 0
  return buildApiUrl(`/api/files/image?path=${encodePath(path)}&v=${version}`)
}

/** 128×128 缩略图地址；版本号让图片修改后自动绕过浏览器与后端旧缓存。 */
export function buildThumbnailUrl(path: string, modifiedAt: number): string {
  const version = Number.isFinite(modifiedAt) ? Math.max(0, Math.trunc(modifiedAt)) : 0
  return buildApiUrl(`/api/files/thumbnail?path=${encodePath(path)}&v=${version}`)
}

export function makeDirectory(path: string): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/mkdir', {
    method: 'POST',
    body: JSON.stringify({ path }),
  })
}

export function createFile(path: string): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/touch', {
    method: 'POST',
    body: JSON.stringify({ path }),
  })
}

export function renameEntry(path: string, newName: string): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/rename', {
    method: 'POST',
    body: JSON.stringify({ path, newName }),
  })
}

export function deleteEntries(paths: string[], recursive: boolean): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/delete', {
    method: 'POST',
    body: JSON.stringify({ paths, recursive }),
  })
}

/**
 * 复制条目；`operationId` 用于在请求未返回时并行轮询进度并可中途取消。
 * 不带时保持既有的无进度同步行为。
 */
export function copyEntries(
  sources: string[],
  target: string,
  overwrite = false,
  operationId?: string,
): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/copy', {
    method: 'POST',
    body: JSON.stringify({ sources, target, overwrite, operationId }),
  })
}

/** 移动条目；进度与取消语义同 {@link copyEntries}。 */
export function moveEntries(
  sources: string[],
  target: string,
  overwrite = false,
  operationId?: string,
): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/move', {
    method: 'POST',
    body: JSON.stringify({ sources, target, overwrite, operationId }),
  })
}

/** 读取粘贴进度；操作不存在、已过期或不属于当前账号时返回 404。 */
export function getFileTransferProgress(operationId: string): Promise<FileTransferProgress> {
  return request<FileTransferProgress>(
    `/api/files/transfer-progress/${encodeURIComponent(operationId)}`,
  )
}

/** 取消粘贴：终止辅助进程，已复制的条目保留。 */
export function cancelFileTransfer(operationId: string): Promise<FileTransferCancelResponse> {
  return request<FileTransferCancelResponse>(
    `/api/files/transfer-progress/${encodeURIComponent(operationId)}/cancel`,
    { method: 'POST' },
  )
}

/** 下载地址：由浏览器直接发起 GET，会话 Cookie 随同源请求自动携带。 */
export function buildDownloadUrl(path: string): string {
  return buildApiUrl(`/api/files/download?path=${encodePath(path)}`)
}

/** 读取文本文件内容；非 UTF-8 或超过大小上限时后端返回 400/413。 */
export function readTextFile(path: string): Promise<TextFileResponse> {
  return request<TextFileResponse>(`/api/files/text?path=${encodePath(path)}`)
}

/**
 * 保存文本文件。
 * `expectedModifiedAt` 为读取/上次保存时的修改时间，服务端不一致时返回 409；
 * 传 0 表示强制覆盖（用户确认后）。
 */
export function saveTextFile(
  path: string,
  content: string,
  expectedModifiedAt: number,
  hasBom: boolean,
): Promise<TextFileSaveResponse> {
  return request<TextFileSaveResponse>('/api/files/text', {
    method: 'POST',
    body: JSON.stringify({ path, content, expectedModifiedAt, hasBom }),
  })
}

/** 上传进度：`percent` 已夹紧在 0–100，`total` 未知时为 0。 */
export interface UploadProgress {
  loaded: number
  total: number
  percent: number
}

function parseUploadError(status: number, body: string): never {
  let problem: ProblemDetails = {}
  try {
    problem = JSON.parse(body) as ProblemDetails
  } catch {
    problem = {}
  }

  throw new ApiError(status, problem.title ?? `上传失败（${status}）`, problem.detail)
}

/**
 * 上传单个文件。
 * multipart 不能复用 request() 的 JSON 默认头；改用 XMLHttpRequest 是因为 fetch 无法
 * 上报上传进度（`xhr.upload.onprogress`）。会话仍通过 Cookie（withCredentials）传递。
 */
export function uploadFile(
  directory: string,
  file: File,
  overwrite = false,
  onProgress?: (progress: UploadProgress) => void,
): Promise<FileMutationResponse> {
  const form = new FormData()
  form.append('file', file, file.name)

  const suffix = overwrite ? '&overwrite=true' : ''
  const url = buildApiUrl(`/api/files/upload?path=${encodePath(directory)}${suffix}`)

  return new Promise<FileMutationResponse>((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', url)
    xhr.withCredentials = true

    xhr.upload.addEventListener('progress', (event) => {
      const total = event.lengthComputable ? event.total : 0
      const percent = total > 0 ? Math.min(100, Math.round((event.loaded / total) * 100)) : 0
      onProgress?.({ loaded: event.loaded, total, percent })
    })

    xhr.addEventListener('load', () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        let data: FileMutationResponse = { ok: true }
        try {
          data = JSON.parse(xhr.responseText) as FileMutationResponse
        } catch {
          data = { ok: true }
        }

        onProgress?.({ loaded: file.size, total: file.size, percent: 100 })
        resolve(data)
        return
      }

      try {
        parseUploadError(xhr.status, xhr.responseText)
      } catch (error) {
        reject(error)
      }
    })

    xhr.addEventListener('error', () => reject(new ApiError(0, '上传失败：网络错误')))
    xhr.addEventListener('abort', () => reject(new ApiError(0, '上传已取消')))
    xhr.send(form)
  })
}
