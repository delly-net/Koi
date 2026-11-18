/**
 * 文本编辑支持的文件后缀名、着色方案与默认映射。
 *
 * 运行期数据来自后端 SQLite 独立表 `EditorFileTypes`（`GET /api/editor/associations`），
 * 本文件只保存方案标识、默认规则与纯数据工具函数，不导入 CodeMirror，
 * 避免编辑器依赖被打进首屏 bundle。
 *
 * 方案标识与默认后缀→方案映射必须与后端 `api/Editor/EditorSyntaxSchemes.cs` 保持一致。
 */

/** 一条后缀规则及它的着色方案；extension 统一小写。 */
export interface EditorFileType {
  extension: string
  scheme: string
}

/** 内置着色方案（顺序与后端一致）。 */
export const SYNTAX_SCHEMES = [
  { id: 'plaintext', label: '纯文本' },
  { id: 'config', label: '配置文件（INI/Properties）' },
  { id: 'json', label: 'JSON' },
  { id: 'yaml', label: 'YAML' },
  { id: 'xml', label: 'XML' },
  { id: 'shell', label: 'Shell' },
  { id: 'nginx', label: 'Nginx' },
  { id: 'systemd', label: 'systemd 单元' },
  { id: 'dockerfile', label: 'Dockerfile' },
  { id: 'env', label: '环境变量（.env）' },
] as const

/** 默认着色方案：未识别后缀一律纯文本。 */
export const DEFAULT_SCHEME = 'plaintext'

const KNOWN_SCHEMES = new Set<string>(SYNTAX_SCHEMES.map((scheme) => scheme.id))

/** 内置默认后缀规则（与后端 `DefaultExtensions` 一致）。 */
const DEFAULT_EXTENSIONS = [
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

/** 判断方案标识是否为内置方案。 */
export function isKnownScheme(scheme: string): boolean {
  return KNOWN_SCHEMES.has(scheme)
}

/** 按后缀/文件名规则给出默认着色方案；未识别的规则回退纯文本。 */
export function defaultSchemeFor(extension: string): string {
  switch (extension.trim().toLowerCase()) {
    case '.json':
    case '.jsonc':
      return 'json'
    case '.yml':
    case '.yaml':
      return 'yaml'
    case '.xml':
      return 'xml'
    case '.ini':
    case '.conf':
    case '.cfg':
    case '.properties':
    case '.toml':
    case '.editorconfig':
      return 'config'
    case '.env':
      return 'env'
    case '.sh':
    case '.bash':
    case '.zsh':
    case '.fish':
      return 'shell'
    case '.service':
    case '.timer':
    case '.desktop':
      return 'systemd'
    case '.dockerfile':
    case 'dockerfile':
      return 'dockerfile'
    default:
      return DEFAULT_SCHEME
  }
}

/** 后端接口不可用时的兜底规则：默认后缀 + 默认方案。 */
export const DEFAULT_EDITOR_FILE_TYPES: EditorFileType[] = DEFAULT_EXTENSIONS.map((extension) => ({
  extension,
  scheme: defaultSchemeFor(extension),
}))

/**
 * 规范化后端或界面返回的规则列表：去空白、转小写、去重保序；
 * 未知方案回退纯文本。非数组返回 null（调用方应保留当前值）。
 */
export function normalizeFileTypes(raw: unknown): EditorFileType[] | null {
  if (!Array.isArray(raw)) {
    return null
  }

  const result: EditorFileType[] = []
  const seen = new Set<string>()

  for (const item of raw) {
    if (!item || typeof item !== 'object') {
      continue
    }

    const record = item as Record<string, unknown>
    const extension = typeof record.extension === 'string' ? record.extension.trim().toLowerCase() : ''
    if (!extension || seen.has(extension)) {
      continue
    }

    seen.add(extension)
    const scheme = typeof record.scheme === 'string' ? record.scheme.trim().toLowerCase() : ''
    result.push({ extension, scheme: isKnownScheme(scheme) ? scheme : DEFAULT_SCHEME })
  }

  return result
}

/**
 * 在规则列表中查找匹配文件名的第一条规则。
 *
 * 匹配语义与旧版文本关联一致：以 `.` 开头的规则按「完整文件名或后缀」匹配
 *（`.txt`、`.gitignore`、`.tar.gz`），其余规则按「完整文件名或 `.规则` 后缀」匹配
 *（`dockerfile`、`makefile`）。未命中返回 undefined。
 */
export function findFileTypeRule(
  nameOrPath: string,
  rules: readonly EditorFileType[],
): EditorFileType | undefined {
  const name = nameOrPath.replace(/\\/g, '/').split('/').pop()?.toLowerCase() ?? ''
  if (!name) {
    return undefined
  }

  return rules.find((rule) => {
    if (!rule.extension) {
      return false
    }

    if (rule.extension.startsWith('.')) {
      return name === rule.extension || name.endsWith(rule.extension)
    }

    return name === rule.extension || name.endsWith(`.${rule.extension}`)
  })
}
