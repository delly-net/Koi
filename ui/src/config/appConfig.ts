/**
 * 应用运行时配置：由 public/conf/setting.json 提供，构建后可直接修改以切换后端地址，
 * 无需重新构建前端。配置在 main.ts 中先于路由安装加载。
 */
export interface FileAssociationsConfig {
  /** 按文本方式打开的扩展名/文件名规则；由系统配置提供。 */
  text?: unknown
}

export interface AppConfig {
  /** 后端对外根地址（不含 /api）；空串表示同源。 */
  baseUrl?: string
  /** 文件关联信息：决定哪些文件可通过【打开】/【编辑文本】进入在线编辑。 */
  fileAssociations?: FileAssociationsConfig
}

/** 配置文件通过 HTTP 直接访问，不参与打包。 */
const CONFIG_URL = '/conf/setting.json'
/** 默认后端根地址：空串 = 同源（请求走相对路径 /api/...）。 */
const DEFAULT_BACKEND_ROOT = ''
/** 后端业务端点统一前缀（与 api/auth.ts 中的请求路径一致）。 */
const API_PREFIX = '/api'

/**
 * 未提供有效 fileAssociations.text 时使用的内置文本关联规则。
 * 部署配置（ui/public/conf 与 deploy/linux/conf）应显式列出，这里是启动兜底。
 */
const DEFAULT_TEXT_EXTENSIONS = [
  '.txt',
  '.md',
  '.markdown',
  '.log',
  '.csv',
  '.tsv',
  '.json',
  '.jsonc',
  '.xml',
  '.yml',
  '.yaml',
  '.ini',
  '.conf',
  '.cfg',
  '.properties',
  '.toml',
  '.env',
  '.sh',
  '.bash',
  '.zsh',
  '.fish',
  '.py',
  '.js',
  '.mjs',
  '.cjs',
  '.jsx',
  '.ts',
  '.tsx',
  '.vue',
  '.html',
  '.htm',
  '.css',
  '.scss',
  '.less',
  '.sql',
  '.java',
  '.c',
  '.h',
  '.cpp',
  '.hpp',
  '.cs',
  '.go',
  '.rs',
  '.php',
  '.rb',
  '.pl',
  '.lua',
  '.service',
  '.timer',
  '.desktop',
  '.dockerfile',
  '.gitignore',
  '.gitattributes',
  '.editorconfig',
  'dockerfile',
  'makefile',
]

let backendRoot = DEFAULT_BACKEND_ROOT
let textExtensions: string[] = [...DEFAULT_TEXT_EXTENSIONS]

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

/** 当前生效的文本文件关联规则（副本，避免调用方修改内部状态）。 */
export function getTextExtensions(): string[] {
  return [...textExtensions]
}

/** 规范化文件关联规则：去空白、转小写、去重；非数组返回 null。 */
export function normalizeExtensions(raw: unknown): string[] | null {
  if (!Array.isArray(raw)) {
    return null
  }

  const result: string[] = []
  for (const item of raw) {
    if (typeof item !== 'string') {
      continue
    }

    const value = item.trim().toLowerCase()
    if (value && !result.includes(value)) {
      result.push(value)
    }
  }

  return result
}

/** 应用文本文件关联规则（空数组表示不把任何文件视为文本）。 */
export function setTextExtensions(rules: string[]): void {
  textExtensions = [...rules]
  console.info(`[配置] 文本文件关联：${textExtensions.length} 条规则`)
}

/**
 * 判断文件名或路径是否属于配置中的文本类型。
 * 以 `.` 开头的规则按「完整文件名或扩展名后缀」匹配（支持 `.gitignore`、`.tar.gz`）；
 * 其余规则按「完整文件名或 `.<规则>` 后缀」匹配（支持 `dockerfile`、`makefile`）。
 */
export function isTextFile(nameOrPath: string): boolean {
  const name = nameOrPath.replace(/\\/g, '/').split('/').pop()?.toLowerCase() ?? ''
  if (!name) {
    return false
  }

  return textExtensions.some((rule) => {
    if (rule.startsWith('.')) {
      return name === rule || name.endsWith(rule)
    }

    return name === rule || name.endsWith(`.${rule}`)
  })
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

    const textRules = normalizeExtensions(config.fileAssociations?.text)
    if (textRules) {
      setTextExtensions(textRules)
    } else {
      console.warn(`[配置] ${CONFIG_URL} 未提供有效的 fileAssociations.text，使用内置文本关联`)
    }
  } catch (error) {
    console.warn(`[配置] 加载 ${CONFIG_URL} 失败，使用同源默认值：`, error)
  }
}
