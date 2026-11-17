import { ref } from 'vue'
import {
  DEFAULT_EDITOR_FILE_TYPES,
  DEFAULT_SCHEME,
  findFileTypeRule,
  normalizeFileTypes,
  type EditorFileType,
} from './editorFileTypes'

/**
 * 应用运行时配置：由 public/conf/setting.json 提供，构建后可直接修改以切换后端地址，
 * 无需重新构建前端。配置在 main.ts 中先于路由安装加载。
 *
 * 文本文件关联规则与着色方案已改为由后端 SQLite 独立表 EditorFileTypes 维护，
 * setting.json 中的 fileAssociations.text 不再读取。
 */
export interface AppConfig {
  /** 后端对外根地址（不含 /api）；空串表示同源。 */
  baseUrl?: string
}

/** 配置文件通过 HTTP 直接访问，不参与打包。 */
const CONFIG_URL = '/conf/setting.json'
/** 默认后端根地址：空串 = 同源（请求走相对路径 /api/...）。 */
const DEFAULT_BACKEND_ROOT = ''
/** 后端业务端点统一前缀（与 api/auth.ts 中的请求路径一致）。 */
const API_PREFIX = '/api'

let backendRoot = DEFAULT_BACKEND_ROOT
/**
 * 当前生效的后缀规则（后缀 + 着色方案）。使用 ref 保存，文件管理模板中的 isTextFile()
 * 与编辑器的着色方案都会随 root 保存设置而响应式刷新。
 */
const fileTypes = ref<EditorFileType[]>(DEFAULT_EDITOR_FILE_TYPES.map((item) => ({ ...item })))

/**
 * 归一化后端根地址，统一为「不含 /api 的根地址」：
 *   "http://host:5000/"        → "http://host:5000"
 *   "http://host:5000/api"     → "http://host:5000"（兼容误配）
 *   "" / "/"                   → ""（同源）
 */
export function normalizeBackendRoot(raw: unknown): string {
  if (typeof raw !== 'string') {
    return DEFAULT_BACKEND_ROOT
  }

  let root = raw.trim().replace(/\/+$/, '')
  if (root === API_PREFIX) {
    return DEFAULT_BACKEND_ROOT
  }
  if (root.endsWith(API_PREFIX)) {
    root = root.slice(0, -API_PREFIX.length).replace(/\/+$/, '')
  }

  return root
}

/**
 * 设置后端根地址。空串是合法值（同源），调用方须以 typeof 判空而非 falsy。
 */
export function setBaseUrl(url: string): void {
  backendRoot = normalizeBackendRoot(url)
  console.info(`[配置] 后端根地址：${backendRoot || '(同源)'}`)
}

/** 获取后端根地址（不含 /api），供 WebSocket、静态资源等根路径端点复用。 */
export function getBackendRoot(): string {
  return backendRoot
}

/** 当前生效的后缀规则（副本，避免调用方修改内部状态）。 */
export function getEditorFileTypes(): EditorFileType[] {
  return fileTypes.value.map((item) => ({ ...item }))
}

/** 应用后缀规则（空数组表示不把任何文件视为文本，编辑器也没有可着色的后缀）。 */
export function setEditorFileTypes(rules: EditorFileType[]): void {
  fileTypes.value = normalizeFileTypes(rules) ?? []
  console.info(`[配置] 文本编辑后缀规则：${fileTypes.value.length} 条`)
}

/**
 * 判断文件名或路径是否属于配置中的文本类型。
 * 以 `.` 开头的规则按「完整文件名或扩展名后缀」匹配（支持 `.gitignore`、`.tar.gz`）；
 * 其余规则按「完整文件名或 `.<规则>` 后缀」匹配（支持 `dockerfile`、`makefile`）。
 */
export function isTextFile(nameOrPath: string): boolean {
  return findFileTypeRule(nameOrPath, fileTypes.value) !== undefined
}

/**
 * 取文件名对应的着色方案；未命中任何后缀规则时回退纯文本。
 * Nginx 配置页签由调用方（EditorView）强制使用 `nginx` 方案。
 */
export function resolveSyntaxScheme(nameOrPath: string): string {
  return findFileTypeRule(nameOrPath, fileTypes.value)?.scheme ?? DEFAULT_SCHEME
}

/** 统一拼接请求地址：绝对 URL 原样返回，其余路径前置后端根地址。 */
export function buildApiUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path
  }
  return `${backendRoot}${path.startsWith('/') ? path : `/${path}`}`
}

/**
 * 由 HTTP 路径推导 WebSocket 地址：
 *   "wss://host/path" 原样返回；"https://host/path" 转为 wss；
 *   同源相对路径按当前页面协议与主机补全（http → ws，https → wss）。
 */
export function buildWebSocketUrl(path: string): string {
  if (/^wss?:\/\//i.test(path)) {
    return path
  }

  const url = buildApiUrl(path)
  if (/^https?:\/\//i.test(url)) {
    return url.replace(/^http/i, 'ws')
  }

  const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${window.location.host}${url.startsWith('/') ? url : `/${url}`}`
}

/**
 * 加载配置文件并应用 baseUrl。
 * 加载失败、状态码非 2xx、字段缺失或类型不符时仅告警，保持同源默认值继续启动。
 */
export async function loadAppConfig(): Promise<void> {
  try {
    const response = await fetch(CONFIG_URL, { cache: 'no-store' })
    if (!response.ok) {
      throw new Error(`HTTP ${response.status} ${response.statusText}`)
    }

    const config = (await response.json()) as AppConfig
    if (typeof config.baseUrl === 'string') {
      setBaseUrl(config.baseUrl)
    } else {
      console.warn(`[配置] ${CONFIG_URL} 中 baseUrl 不是字符串，使用同源默认值`)
    }
  } catch (error) {
    console.warn(`[配置] 加载 ${CONFIG_URL} 失败，使用同源默认值：`, error)
  }
}
