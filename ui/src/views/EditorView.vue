<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type InputInstance } from 'element-plus'
import { FolderOpened, Setting } from '@element-plus/icons-vue'
import { useEditorStore, type EditorTab } from '@/stores/editor'
import { getFileRoots } from '@/api/files'
import { getEditorSettings, saveEditorSettings, type EditorFileTypes } from '@/api/editor'
import FilePickerDialog from '@/components/files/FilePickerDialog.vue'
import { resolveSyntaxScheme, setEditorFileTypes } from '@/config/appConfig'
import {
  SYNTAX_SCHEMES,
  normalizeFileTypes,
  type EditorFileType,
} from '@/config/editorFileTypes'
import { ApiError } from '@/api/http'

// CodeMirror 及其语言包只随编辑器页签按需加载，避免进入首屏 bundle。
const CodeEditor = defineAsyncComponent(() => import('@/components/editor/CodeEditor.vue'))

const editor = useEditorStore()

const activeTab = computed<EditorTab | null>(
  () => editor.tabs.find((tab) => tab.key === editor.activeKey) ?? null,
)

/** el-tabs 的 v-model 可能是 string | number，这里统一收敛为字符串标签键。 */
const activeKey = computed({
  get: () => editor.activeKey,
  set: (value: string | number) => {
    editor.activeKey = String(value)
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

/** Nginx 配置页签固定使用 Nginx 方案，其余按后缀规则匹配；未命中回退纯文本。 */
function schemeFor(tab: EditorTab): string {
  return tab.source === 'nginx' ? 'nginx' : resolveSyntaxScheme(tab.name)
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
    const data = await editor.readTabContent(tab)
    tab.content = data.content
    tab.savedContent = data.content
    tab.modifiedAt = data.modifiedAt
    tab.size = data.size
    tab.hasBom = data.hasBom
    if (data.path) {
      tab.path = data.path
    }
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
    const data = await editor.saveTabContent(tab, force ? 0 : tab.modifiedAt)
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
  const key = String(name)
  const tab = editor.tabs.find((item) => item.key === key)
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

  editor.closeTab(key)
}

function onBeforeUnload(event: BeforeUnloadEvent): void {
  if (editor.tabs.some((tab) => isDirty(tab))) {
    event.preventDefault()
    event.returnValue = ''
  }
}

// ---------------- 按路径打开文件 ----------------
const openDialog = ref(false)
const openPath = ref('')
const openLoading = ref(false)
const openInput = ref<InputInstance | null>(null)
const pickerVisible = ref(false)
const pickerInitial = ref('')
const homePath = ref('')

function joinPath(directory: string, name: string): string {
  return directory === '/' ? `/${name}` : `${directory.replace(/\/+$/, '')}/${name}`
}

/** 惰性读取家目录，用于展开 ~ 与确定文件浏览的起始目录；失败时保持为空。 */
async function ensureHomePath(): Promise<string> {
  if (homePath.value) {
    return homePath.value
  }

  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
  } catch {
    homePath.value = ''
  }

  return homePath.value
}

/**
 * 把输入解析为绝对路径：`/` 开头原样使用，`~` 与 `~/...` 展开为家目录。
 * 编辑页签以完整路径为键，因此相对路径没有可用的基准目录，直接提示用户。
 */
async function resolveOpenPath(raw: string): Promise<string | null> {
  const value = raw.trim()
  if (!value) {
    ElMessage.warning('路径不能为空')
    return null
  }

  if (value === '~' || value.startsWith('~/')) {
    const home = await ensureHomePath()
    if (!home) {
      ElMessage.warning('尚未获取家目录，无法展开 ~')
      return null
    }

    const rest = value.slice(1).replace(/^\/+/, '')
    return rest ? joinPath(home, rest) : home
  }

  if (value.startsWith('/')) {
    return value
  }

  ElMessage.warning('请输入以 / 开头的绝对路径，或使用 ~ 表示家目录')
  return null
}

function openOpenDialog(): void {
  openDialog.value = true
  void ensureHomePath()
  void nextTick(() => openInput.value?.focus())
}

/** 读取失败会被记录到新页签的 error 上，由编辑器内展示并提供重试。 */
async function submitOpen(): Promise<void> {
  if (openLoading.value) {
    return
  }

  const target = await resolveOpenPath(openPath.value)
  if (!target) {
    return
  }

  openLoading.value = true
  try {
    openDialog.value = false
    openPath.value = ''
    await editor.openFile(target)
  } finally {
    openLoading.value = false
  }
}

/** 浏览起点：当前输入路径的父目录；无法解析时交给浏览对话框使用家目录。 */
async function pickerStartDirectory(): Promise<string> {
  const value = openPath.value.trim()
  if (!value) {
    return ''
  }

  let resolved: string
  if (value === '~' || value.startsWith('~/')) {
    const home = await ensureHomePath()
    if (!home) {
      return ''
    }

    const rest = value.slice(1).replace(/^\/+/, '')
    resolved = rest ? joinPath(home, rest) : home
  } else if (value.startsWith('/')) {
    resolved = value
  } else {
    return ''
  }

  resolved = resolved.replace(/\/+$/, '')
  if (!resolved) {
    return '/'
  }

  const index = resolved.lastIndexOf('/')
  return index <= 0 ? '/' : resolved.slice(0, index)
}

async function openPicker(): Promise<void> {
  pickerInitial.value = await pickerStartDirectory()
  pickerVisible.value = true
}

/** 浏览对话框确认后只回填路径，由用户再点【打开】确认。 */
function onPicked(path: string): void {
  openPath.value = path
  pickerVisible.value = false
  void nextTick(() => openInput.value?.focus())
}

// ---------------- 文本编辑设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<EditorFileTypes | null>(null)
const settingsForm = reactive({
  fileTypes: [] as EditorFileType[],
})

function applySettingsForm(value: EditorFileTypes): void {
  settingsForm.fileTypes = value.fileTypes.map((item) => ({ ...item }))
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getEditorSettings()
    settingsSnapshot.value = value
    applySettingsForm(value)
    settingsAvailable.value = true
  } catch {
    settingsAvailable.value = false
  } finally {
    settingsLoading.value = false
  }
}

async function openSettings(): Promise<void> {
  if (!settingsSnapshot.value) {
    await loadSettingsAvailability()
  }

  if (settingsSnapshot.value) {
    applySettingsForm(settingsSnapshot.value)
    settingsVisible.value = true
  }
}

function addFileType(): void {
  settingsForm.fileTypes.push({ extension: '', scheme: 'config' })
}

function removeFileType(index: number): void {
  settingsForm.fileTypes.splice(index, 1)
}

async function saveSettings(): Promise<void> {
  if (settingsForm.fileTypes.some((item) => !item.extension.trim())) {
    ElMessage.warning('请填写空的后缀名，或删除该行规则')
    return
  }

  // 规范化：去空白、转小写、去重；后端仍会再次校验。
  const fileTypes = normalizeFileTypes(settingsForm.fileTypes) ?? []

  settingsSaving.value = true
  try {
    const saved = await saveEditorSettings({ fileTypes })
    settingsSnapshot.value = saved
    applySettingsForm(saved)

    // 立即应用到文件管理的文本关联判断与编辑器着色，无需刷新页面。
    setEditorFileTypes(saved.fileTypes)
    settingsVisible.value = false
    ElMessage.success('设置已保存并立即生效')
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

onMounted(() => {
  window.addEventListener('beforeunload', onBeforeUnload)
  void loadSettingsAvailability()
})
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))
</script>

<template>
  <div class="editor-view">
    <div class="toolbar">
      <span class="title">文本编辑</span>
      <span v-if="activeTab" class="path" :title="activeTab.path">{{ activeTab.path }}</span>
      <el-tag v-if="activeTab?.source === 'nginx'" type="warning" effect="plain" size="small">
        Nginx 配置
      </el-tag>

      <div class="actions">
        <el-button :icon="FolderOpened" :loading="openLoading" @click="openOpenDialog">
          打开
        </el-button>
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
        <el-tooltip v-if="settingsAvailable" content="文本编辑设置" placement="bottom">
          <el-button
            class="settings-button"
            :icon="Setting"
            circle
            :loading="settingsLoading"
            aria-label="文本编辑设置"
            @click="openSettings"
          />
        </el-tooltip>
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
        :key="tab.key"
        :label="tabLabel(tab)"
        :name="tab.key"
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

          <CodeEditor
            v-else
            v-model="tab.content"
            class="editor-host"
            :scheme="schemeFor(tab)"
            :active="tab.key === editor.activeKey"
            @save="saveTab(tab)"
          />
        </div>
      </el-tab-pane>
    </el-tabs>

    <el-dialog
      v-model="openDialog"
      title="打开文件"
      width="560px"
      :close-on-click-modal="false"
      @closed="openPath = ''"
    >
      <el-form label-position="top" @submit.prevent>
        <el-form-item label="文件路径">
          <el-input
            ref="openInput"
            v-model="openPath"
            clearable
            placeholder="如 /etc/nginx/nginx.conf 或 ~/notes.txt"
            @keyup.enter="submitOpen"
          >
            <template #append>
              <el-button :icon="FolderOpened" @click="openPicker">浏览...</el-button>
            </template>
          </el-input>
        </el-form-item>
      </el-form>
      <div class="open-hint">
        支持 / 开头的绝对路径，以及 ~（家目录）与 ~/子目录；可访问范围仍由登录用户的系统权限与文件管理允许根决定。
      </div>
      <template #footer>
        <el-button @click="openDialog = false">取消</el-button>
        <el-button type="primary" :loading="openLoading" @click="submitOpen">打开</el-button>
      </template>
    </el-dialog>

    <FilePickerDialog
      v-model="pickerVisible"
      :initial-path="pickerInitial"
      :home-path="homePath"
      @select="onPicked"
    />

    <el-drawer
      v-model="settingsVisible"
      title="文本编辑设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 独立表（EditorFileTypes），保存后立即生效，无需重启服务。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-form-item label="支持的文件后缀名与着色方案">
          <el-table
            :data="settingsForm.fileTypes"
            size="small"
            max-height="420"
            class="file-type-table"
          >
            <el-table-column label="后缀名" min-width="150">
              <template #default="{ row }">
                <el-input v-model="row.extension" size="small" placeholder="如 .json" />
              </template>
            </el-table-column>
            <el-table-column label="着色方案" min-width="170">
              <template #default="{ row }">
                <el-select v-model="row.scheme" size="small" class="scheme-select">
                  <el-option
                    v-for="scheme in SYNTAX_SCHEMES"
                    :key="scheme.id"
                    :label="scheme.label"
                    :value="scheme.id"
                  />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="" width="56" align="center">
              <template #default="{ $index }">
                <el-button link type="danger" @click="removeFileType($index)">删除</el-button>
              </template>
            </el-table-column>
          </el-table>
          <el-button class="add-file-type" size="small" @click="addFileType">添加规则</el-button>
          <div class="form-hint">
            每条规则同时决定「文件管理」中哪些文件可通过【打开】/【编辑文本】进入在线编辑，
            以及编辑器使用的着色方案。不区分大小写；以 . 开头的规则按完整文件名或后缀匹配
            （如 .txt、.gitignore），不带点的规则按完整文件名或 .规则 后缀匹配
            （如 dockerfile、makefile）。未识别后缀使用纯文本方案；
            Nginx 配置页签固定使用 Nginx 方案。留空表示不把任何文件视为文本。
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="settingsSaving" @click="settingsVisible = false">取消</el-button>
        <el-button type="primary" :loading="settingsSaving" @click="saveSettings">保存</el-button>
      </template>
    </el-drawer>
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
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.settings-button {
  flex: none;
}

.settings-tip {
  margin: 0 0 8px;
  padding: 8px 12px;
  background: #f4f8ff;
  border: 1px solid #d9ecff;
  border-radius: 6px;
  color: #5c6b82;
  font-size: 12px;
  line-height: 1.6;
}

.file-type-table {
  width: 100%;
}

.scheme-select {
  width: 100%;
}

.add-file-type {
  margin-top: 8px;
}

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.6;
}

.open-hint {
  color: #8a919f;
  font-size: 12px;
  line-height: 1.6;
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

.editor-host {
  flex: 1;
  min-height: 0;
}
</style>
