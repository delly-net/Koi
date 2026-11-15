import { normalizeExtensions, setTextExtensions } from '@/config/appConfig'
import { request } from './http'

/** 文本编辑配置：决定文件管理中哪些文件可通过【打开】/【编辑文本】进入在线编辑。 */
export interface EditorSettings {
  /** 扩展名/文件名规则；空数组表示不把任何文件视为文本。 */
  textExtensions: string[]
}

/** 读取文本关联规则（所有登录用户可读）。 */
export function getEditorAssociations(): Promise<EditorSettings> {
  return request<EditorSettings>('/api/editor/associations')
}

/** 读取文本编辑设置；非 root 账号会收到 403。 */
export function getEditorSettings(): Promise<EditorSettings> {
  return request<EditorSettings>('/api/editor/settings')
}

/** 保存文本编辑设置；成功后立即生效。 */
export function saveEditorSettings(payload: EditorSettings): Promise<EditorSettings> {
  return request<EditorSettings>('/api/editor/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

/**
 * 从后端拉取文本关联规则并应用到前端（登录/会话恢复后调用）。
 * 接口不可用、未登录或返回非法内容时静默保留当前规则（内置默认或上次生效值）。
 */
export async function loadEditorAssociations(): Promise<void> {
  try {
    const data = await getEditorAssociations()
    const rules = normalizeExtensions(data.textExtensions)
    if (rules) {
      setTextExtensions(rules)
    }
  } catch (error) {
    console.warn('[配置] 读取文本文件关联失败，沿用当前规则：', error)
  }
}
