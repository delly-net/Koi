import { StreamLanguage } from '@codemirror/language'
import type { Extension } from '@codemirror/state'
import { json } from '@codemirror/lang-json'
import { xml } from '@codemirror/lang-xml'
import { yaml } from '@codemirror/lang-yaml'
import { properties } from '@codemirror/legacy-modes/mode/properties'
import { shell } from '@codemirror/legacy-modes/mode/shell'
import { nginx } from '@codemirror/legacy-modes/mode/nginx'
import { dockerFile } from '@codemirror/legacy-modes/mode/dockerfile'

/**
 * 内置着色方案 → CodeMirror 语言扩展。
 *
 * 只被 CodeEditor 异步组件导入，避免 CodeMirror 依赖进入首屏 bundle。
 * `config` / `systemd` / `env` 使用 INI/Properties 词法（支持 `[Section]` 与 key=value），
 * 与后端的方案标识一一对应。
 */
const propertiesLanguage = StreamLanguage.define(properties)
const shellLanguage = StreamLanguage.define(shell)
const nginxLanguage = StreamLanguage.define(nginx)
const dockerfileLanguage = StreamLanguage.define(dockerFile)

/** 按方案标识返回语言扩展；未知方案或纯文本返回空扩展（不着色）。 */
export function languageForScheme(scheme: string): Extension {
  switch (scheme) {
    case 'json':
      return json()
    case 'yaml':
      return yaml()
    case 'xml':
      return xml()
    case 'config':
    case 'systemd':
    case 'env':
      return propertiesLanguage
    case 'shell':
      return shellLanguage
    case 'nginx':
      return nginxLanguage
    case 'dockerfile':
      return dockerfileLanguage
    default:
      return []
  }
}
