/**
 * 应用运行时配置：由 public/conf/setting.json 提供，构建后可直接修改以切换后端地址，
 * 无需重新构建前端。配置在 main.ts 中先于路由安装加载。
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

/** 统一拼接请求地址：绝对 URL 原样返回，其余路径前置后端根地址。 */
export function buildApiUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path
  }
  return `${backendRoot}${path.startsWith('/') ? path : `/${path}`}`
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
