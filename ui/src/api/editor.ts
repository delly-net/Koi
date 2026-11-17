import { setEditorFileTypes } from '@/config/appConfig'
import { normalizeFileTypes, type EditorFileType } from '@/config/editorFileTypes'
import { request } from './http'

/**
 * 文本编辑设置：每条规则同时决定「文件管理哪些文件可按文本打开」
 * 与「编辑器使用哪种着色方案」。
 */
export interface EditorFileTypes {
  fileTypes: EditorFileType[]
}

/** 读取后缀规则（所有登录用户可读）。 */
export function getEditorAssociations(): Promise<EditorFileTypes> {
  return request<EditorFileTypes>('/api/editor/associations')
}

/** 读取文本编辑设置；非 root 账号会收到 403。 */
export function getEditorSettings(): Promise<EditorFileTypes> {
  return request<EditorFileTypes>('/api/editor/settings')
}

/** 保存文本编辑设置；成功后立即生效。 */
export function saveEditorSettings(payload: EditorFileTypes): Promise<EditorFileTypes> {
  return request<EditorFileTypes>('/api/editor/settings', {
    method: 'PUT',
    body: JSON.stringify(payload),
  })
}

/**
 * 从后端拉取后缀规则并应用到前端（登录/会话恢复后调用）。
 * 接口不可用、未登录或返回非法内容时静默保留当前规则（内置默认或上次生效值）。
 */
export async function loadEditorAssociations(): Promise<void> {
  try {
    const data = await getEditorAssociations()
    const rules = normalizeFileTypes(data.fileTypes)
    if (rules) {
      setEditorFileTypes(rules)
    }
  } catch (error) {
    console.warn('[配置] 读取文本编辑后缀规则失败，沿用当前规则：', error)
  }
}
