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

export interface FileMutationResponse {
  ok: boolean
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
