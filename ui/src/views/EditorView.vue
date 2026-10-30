<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useEditorStore, type EditorTab } from '@/stores/editor'
import { readTextFile, saveTextFile } from '@/api/files'
import { ApiError } from '@/api/http'

const editor = useEditorStore()

const activeTab = computed<EditorTab | null>(
  () => editor.tabs.find((tab) => tab.path === editor.activePath) ?? null,
)

/** el-tabs 的 v-model 可能是 string | number，这里统一收敛为字符串路径。 */
const activeKey = computed({
  get: () => editor.activePath,
  set: (value: string | number) => {
    editor.activePath = String(value)
  },
})

function isDirty(tab: EditorTab): boolean {
  return tab.content !== tab.savedContent
}

function tabLabel(tab: EditorTab): string {
  if (tab.loading) {
    return `${tab.name}（读取中）`
  }

  return isDirty(tab) ? `${tab.name} ●` : tab.name
}

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    return error.detail ? `${error.message}：${error.detail}` : error.message
  }

  return error instanceof Error ? error.message : '读取文件失败'
}

function showError(error: unknown): void {
  ElMessage.error(errorText(error))
}

async function reloadTab(tab: EditorTab): Promise<void> {
  if (tab.loading) {
    return
  }

  tab.loading = true
  tab.error = ''
  try {
    const data = await readTextFile(tab.path)
    tab.content = data.content
    tab.savedContent = data.content
    tab.modifiedAt = data.modifiedAt
    tab.size = data.size
    tab.hasBom = data.hasBom
  } catch (error) {
    tab.error = errorText(error)
  } finally {
    tab.loading = false
  }
}

type ConflictChoice = 'overwrite' | 'reload' | 'cancel'

/** 保存冲突时的三选一：覆盖保存 / 重新加载 / 取消。 */
async function askConflict(): Promise<ConflictChoice> {
  try {
    await ElMessageBox.confirm(
      '文件已被其他程序修改。覆盖保存会丢弃对方的修改；重新加载会丢弃当前未保存的编辑。',
      '保存冲突',
      {
        type: 'warning',
        distinguishCancelAndClose: true,
        confirmButtonText: '覆盖保存',
        cancelButtonText: '重新加载',
      },
    )
    return 'overwrite'
  } catch (action) {
    return action === 'cancel' ? 'reload' : 'cancel'
  }
}

/** 实际保存流程；force=true 表示用户已确认覆盖，跳过冲突检测。 */
async function performSave(tab: EditorTab, force: boolean): Promise<void> {
  try {
    const data = await saveTextFile(
      tab.path,
      tab.content,
      force ? 0 : tab.modifiedAt,
      tab.hasBom,
    )
    tab.savedContent = tab.content
    tab.modifiedAt = data.modifiedAt
    tab.size = data.size
    ElMessage.success(`已保存：${tab.name}`)
  } catch (error) {
    if (error instanceof ApiError && error.status === 409 && !force) {
      const choice = await askConflict()
      if (choice === 'overwrite') {
        await performSave(tab, true)
      } else if (choice === 'reload') {
        await reloadTab(tab)
      }
      return
    }

    showError(error)
  }
}

async function saveTab(tab: EditorTab): Promise<void> {
  if (tab.saving || tab.loading || tab.error) {
    return
  }

  tab.saving = true
  try {
    await performSave(tab, false)
  } finally {
    tab.saving = false
  }
}

async function requestReload(tab: EditorTab): Promise<void> {
  if (tab.loading) {
    return
  }

  if (isDirty(tab)) {
    try {
      await ElMessageBox.confirm('重新加载会丢弃当前未保存的修改，确定继续吗？', '重新加载', {
        type: 'warning',
        confirmButtonText: '重新加载',
        cancelButtonText: '取消',
      })
    } catch {
      return
    }
  }

  await reloadTab(tab)
}

async function closeTab(name: string | number): Promise<void> {
  const path = String(name)
  const tab = editor.tabs.find((item) => item.path === path)
  if (!tab) {
    return
  }

  if (isDirty(tab)) {
    try {
      await ElMessageBox.confirm(`「${tab.name}」有未保存的修改，确定关闭吗？`, '关闭标签', {
        type: 'warning',
        confirmButtonText: '关闭',
        cancelButtonText: '取消',
      })
    } catch {
      return
    }
  }

  editor.closeTab(path)
}

/** 保存快捷键：在编辑器区域按 Ctrl/Cmd+S。 */
function onSaveShortcut(): void {
  if (activeTab.value) {
    void saveTab(activeTab.value)
  }
}

function onBeforeUnload(event: BeforeUnloadEvent): void {
  if (editor.tabs.some((tab) => isDirty(tab))) {
    event.preventDefault()
    event.returnValue = ''
  }
}

onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))
</script>

<template>
  <div
    class="editor-view"
    @keydown.ctrl.s.prevent="onSaveShortcut"
    @keydown.meta.s.prevent="onSaveShortcut"
  >
    <div class="toolbar">
      <span class="title">文本编辑</span>
      <span v-if="activeTab" class="path" :title="activeTab.path">{{ activeTab.path }}</span>

      <div class="actions">
        <el-button
          type="primary"
          :loading="activeTab?.saving ?? false"
          :disabled="!activeTab || !isDirty(activeTab) || activeTab.loading || !!activeTab.error"
          @click="activeTab && saveTab(activeTab)"
        >
          保存
        </el-button>
        <el-button
          :disabled="!activeTab || activeTab.loading"
          @click="activeTab && requestReload(activeTab)"
        >
          重新加载
        </el-button>
      </div>
    </div>

    <el-empty
      v-if="editor.tabs.length === 0"
      class="empty"
      description="请从「文件管理」行操作菜单选择【编辑文本】打开文本文件"
    />

    <el-tabs v-else v-model="activeKey" type="card" class="tabs" @tab-remove="closeTab">
      <el-tab-pane
        v-for="tab in editor.tabs"
        :key="tab.path"
        :label="tabLabel(tab)"
        :name="tab.path"
        :closable="true"
      >
        <div class="pane">
          <el-alert
            v-if="tab.error"
            type="error"
            :closable="false"
            show-icon
            :title="tab.error"
          >
            <template #default>
              <el-button size="small" @click="reloadTab(tab)">重试</el-button>
            </template>
          </el-alert>

          <el-alert
            v-else-if="tab.loading"
            type="info"
            :closable="false"
            show-icon
            title="正在读取文件…"
          />

          <textarea
            v-else
            v-model="tab.content"
            class="text-editor"
            spellcheck="false"
            autocapitalize="off"
            autocomplete="off"
          ></textarea>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<style scoped>
.editor-view {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 12px 16px;
  background: #f5f7fa;
}

.toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
  margin-bottom: 8px;
}

.title {
  font-size: 16px;
  font-weight: 600;
}

.path {
  overflow: hidden;
  max-width: 42vw;
  color: #909399;
  font-family: ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.actions {
  display: flex;
  gap: 8px;
  margin-left: auto;
}

.empty {
  flex: 1;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.tabs :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: 8px;
}

.tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
}

.tabs :deep(.el-tab-pane) {
  height: 100%;
}

.pane {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
  overflow: hidden;
}

.text-editor {
  flex: 1;
  width: 100%;
  min-height: 0;
  padding: 10px 12px;
  border: none;
  outline: none;
  resize: none;
  background: #ffffff;
  color: #1f2329;
  font-family: ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace;
  font-size: 13px;
  line-height: 1.6;
  tab-size: 4;
  white-space: pre;
  overflow: auto;
}
</style>
