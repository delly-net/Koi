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

export function copyEntries(
  sources: string[],
  target: string,
  overwrite = false,
): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/copy', {
    method: 'POST',
    body: JSON.stringify({ sources, target, overwrite }),
  })
}

export function moveEntries(
  sources: string[],
  target: string,
  overwrite = false,
): Promise<FileMutationResponse> {
  return request<FileMutationResponse>('/api/files/move', {
    method: 'POST',
    body: JSON.stringify({ sources, target, overwrite }),
  })
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

async function parseUploadError(response: Response): Promise<never> {
  let problem: ProblemDetails = {}
  try {
    problem = (await response.json()) as ProblemDetails
  } catch {
    problem = {}
  }

  throw new ApiError(
    response.status,
    problem.title ?? `上传失败（${response.status}）`,
    problem.detail,
  )
}

/** 上传单个文件；multipart 不能复用 request() 的 JSON 默认头，这里单独处理。 */
export async function uploadFile(
  directory: string,
  file: File,
  overwrite = false,
): Promise<FileMutationResponse> {
  const form = new FormData()
  form.append('file', file, file.name)

  const suffix = overwrite ? '&overwrite=true' : ''
  const response = await fetch(
    buildApiUrl(`/api/files/upload?path=${encodePath(directory)}${suffix}`),
    {
      method: 'POST',
      body: form,
      credentials: 'include',
    },
  )

  if (!response.ok) {
    return parseUploadError(response)
  }

  return (await response.json().catch(() => ({ ok: true }))) as FileMutationResponse
}
