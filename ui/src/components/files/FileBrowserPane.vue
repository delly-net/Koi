<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  CopyDocument,
  Document,
  DocumentAdd,
  Folder,
  FolderAdd,
  MoreFilled,
  Refresh,
  Upload,
} from '@element-plus/icons-vue'
import type { FileEntry } from '@/api/files'
import {
  buildDownloadUrl,
  copyEntries,
  createFile,
  deleteEntries,
  listFiles,
  makeDirectory,
  moveEntries,
  renameEntry,
  uploadFile,
} from '@/api/files'
import { ApiError } from '@/api/http'
import { isTextFile } from '@/config/appConfig'
import { useEditorStore } from '@/stores/editor'
import { useFilesClipboardStore } from '@/stores/filesClipboard'
import { useWorkspaceStore } from '@/stores/workspace'

interface Crumb {
  label: string
  path: string
}

interface EditState {
  mode: 'mkdir' | 'touch' | 'rename'
  title: string
  value: string
  target?: FileEntry
}

const props = defineProps<{
  /** 页签初始目录；切换目录后由面板自行维护 currentPath。 */
  initialPath: string
}>()

const emit = defineEmits<{
  /** 当前目录变化，用于同步页签标题。 */
  (e: 'path-change', path: string): void
  /** 请求把某个目录作为新页签打开。 */
  (e: 'open-in-new-tab', path: string): void
}>()

const editor = useEditorStore()
const workspace = useWorkspaceStore()
const filesClipboard = useFilesClipboardStore()

const currentPath = ref('')
const parentPath = ref<string | null>(null)
const entries = ref<FileEntry[]>([])
const truncated = ref(false)
const loading = ref(false)
const uploading = ref(false)
const selection = ref<FileEntry[]>([])
const uploadInput = ref<HTMLInputElement | null>(null)

/** 剪贴板来自共享 store，因此在其它文件页签复制的条目本页签也能直接粘贴。 */
const clipboard = computed(() => filesClipboard.clipboard)

const editDialog = ref(false)
const editLoading = ref(false)
const editState = ref<EditState>({ mode: 'mkdir', title: '', value: '' })

const breadcrumbs = computed<Crumb[]>(() => {
  if (!currentPath.value) {
    return []
  }

  const crumbs: Crumb[] = [{ label: '/', path: '/' }]
  let accumulated = ''
  for (const part of currentPath.value.split('/').filter(Boolean)) {
    accumulated += `/${part}`
    crumbs.push({ label: part, path: accumulated })
  }

  return crumbs
})

const selectedCount = computed(() => selection.value.length)

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

function joinPath(directory: string, name: string): string {
  return directory === '/' ? `/${name}` : `${directory.replace(/\/+$/, '')}/${name}`
}

async function load(path?: string): Promise<void> {
  loading.value = true
  try {
    const data = await listFiles(path)
    currentPath.value = data.path
    parentPath.value = data.parent
    entries.value = data.entries
    truncated.value = data.truncated
    selection.value = []
    emit('path-change', data.path)
  } catch (error) {
    showError(error)
  } finally {
    loading.value = false
  }
}

function onSelectionChange(rows: FileEntry[]): void {
  selection.value = rows
}

/** 打开文本文件：加入编辑器标签并激活「文本编辑」工作区标签；内容在编辑器内异步读取。 */
function openTextEditor(row: FileEntry): void {
  if (row.isDirectory || !isTextFile(row.name)) {
    return
  }

  void editor.openFile(row.path)
  workspace.open('editor')
}

/**
 * 打开条目：目录进入目录；配置中声明为文本的文件进入在线编辑；
 * 其余文件不可打开，只能通过「下载」操作。
 */
function openEntry(row: FileEntry): void {
  if (row.isDirectory) {
    void load(row.path)
    return
  }

  if (isTextFile(row.name)) {
    openTextEditor(row)
  }
}

/** 把目录作为新页签打开；文件不可用。 */
function openInNewTab(row: FileEntry): void {
  if (!row.isDirectory) {
    return
  }

  emit('open-in-new-tab', row.path)
}

function downloadEntry(row: FileEntry): void {
  const link = document.createElement('a')
  link.href = buildDownloadUrl(row.path)
  link.download = row.name
  document.body.appendChild(link)
  link.click()
  link.remove()
}

/** 行操作菜单命令，与模板中 el-dropdown-item 的 command 一一对应。 */
type RowAction = 'open' | 'newtab' | 'edit' | 'download' | 'rename' | 'copy' | 'cut' | 'delete'

function onRowAction(command: RowAction, row: FileEntry): void {
  switch (command) {
    case 'open':
      openEntry(row)
      break
    case 'newtab':
      openInNewTab(row)
      break
    case 'edit':
      openTextEditor(row)
      break
    case 'download':
      downloadEntry(row)
      break
    case 'rename':
      openRename(row)
      break
    case 'copy':
      setClipboard([row], 'copy')
      break
    case 'cut':
      setClipboard([row], 'cut')
      break
    case 'delete':
      void removeEntries([row])
      break
  }
}

function openCreate(mode: 'mkdir' | 'touch'): void {
  editState.value = {
    mode,
    title: mode === 'mkdir' ? '新建文件夹' : '新建文件',
    value: '',
  }
  editDialog.value = true
}

function openRename(row: FileEntry): void {
  editState.value = { mode: 'rename', title: '重命名', value: row.name, target: row }
  editDialog.value = true
}

async function submitEdit(): Promise<void> {
  const value = editState.value.value.trim()
  if (!value) {
    ElMessage.warning('名称不能为空')
    return
  }

  if (value.includes('/')) {
    ElMessage.warning('名称不能包含路径分隔符')
    return
  }

  editLoading.value = true
  try {
    if (editState.value.mode === 'rename') {
      await renameEntry(editState.value.target!.path, value)
    } else {
      const target = joinPath(currentPath.value, value)
      if (editState.value.mode === 'mkdir') {
        await makeDirectory(target)
      } else {
        await createFile(target)
      }
    }

    editDialog.value = false
    ElMessage.success('操作成功')
    await load(currentPath.value)
  } catch (error) {
    showError(error)
  } finally {
    editLoading.value = false
  }
}

async function removeEntries(rows: FileEntry[]): Promise<void> {
  if (rows.length === 0) {
    return
  }

  const recursive = rows.some((row) => row.isDirectory)
  try {
    await ElMessageBox.confirm(
      `确定删除选中的 ${rows.length} 个条目吗？${recursive ? '目录及其内容会被递归删除。' : ''}`,
      '删除确认',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  try {
    await deleteEntries(
      rows.map((row) => row.path),
      recursive,
    )
    ElMessage.success('已删除')
    await load(currentPath.value)
  } catch (error) {
    showError(error)
  }
}

/** 写入共享剪贴板，所有文件页签立即可见。 */
function setClipboard(rows: FileEntry[], mode: 'copy' | 'cut'): void {
  if (rows.length === 0) {
    return
  }

  filesClipboard.set(
    rows.map((row) => row.path),
    mode,
  )
  ElMessage.success(`已${mode === 'copy' ? '复制' : '剪切'} ${rows.length} 项`)
}

async function transfer(
  mode: 'copy' | 'cut',
  sources: string[],
  target: string,
  overwrite: boolean,
): Promise<void> {
  if (mode === 'copy') {
    await copyEntries(sources, target, overwrite)
  } else {
    await moveEntries(sources, target, overwrite)
  }
}

/** 粘贴到当前页签所在目录；来源可以是在其它页签中复制的条目。 */
async function paste(): Promise<void> {
  const clipboardValue = filesClipboard.clipboard
  if (!clipboardValue) {
    return
  }

  try {
    await transfer(
      clipboardValue.mode,
      clipboardValue.sources,
      currentPath.value,
      false,
    )
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      try {
        await ElMessageBox.confirm('目标位置存在同名条目，是否覆盖？', '覆盖确认', {
          type: 'warning',
          confirmButtonText: '覆盖',
          cancelButtonText: '取消',
        })
      } catch {
        return
      }

      try {
        await transfer(
          clipboardValue.mode,
          clipboardValue.sources,
          currentPath.value,
          true,
        )
      } catch (retryError) {
        showError(retryError)
        return
      }
    } else {
      showError(error)
      return
    }
  }

  if (clipboardValue.mode === 'cut') {
    filesClipboard.clear()
  }

  ElMessage.success('粘贴完成')
  await load(currentPath.value)
}

function pickUpload(): void {
  uploadInput.value?.click()
}

async function onUploadChange(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (files.length === 0) {
    return
  }

  uploading.value = true
  let succeeded = 0
  try {
    for (const file of files) {
      try {
        await uploadFile(currentPath.value, file, false)
        succeeded += 1
      } catch (error) {
        if (!(error instanceof ApiError) || error.status !== 409) {
          showError(error)
          continue
        }

        try {
          await ElMessageBox.confirm(`「${file.name}」已存在，是否覆盖？`, '覆盖确认', {
            type: 'warning',
            confirmButtonText: '覆盖',
            cancelButtonText: '跳过',
          })
        } catch {
          continue
        }

        try {
          await uploadFile(currentPath.value, file, true)
          succeeded += 1
        } catch (retryError) {
          showError(retryError)
        }
      }
    }
  } finally {
    uploading.value = false
  }

  if (succeeded > 0) {
    ElMessage.success(`已上传 ${succeeded} 个文件`)
    await load(currentPath.value)
  }
}

function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`
}

function formatTime(milliseconds: number): string {
  if (!milliseconds) {
    return '—'
  }

  return new Date(milliseconds).toLocaleString('zh-CN', { hour12: false })
}

function sortByModified(left: FileEntry, right: FileEntry): number {
  return left.modifiedAt - right.modifiedAt
}

onMounted(() => {
  void load(props.initialPath)
})
</script>

<template>
  <div class="file-pane">
    <div class="toolbar">
      <div class="actions">
        <el-button :icon="FolderAdd" @click="openCreate('mkdir')">新建文件夹</el-button>
        <el-button :icon="DocumentAdd" @click="openCreate('touch')">新建文件</el-button>
        <el-button :icon="Upload" :loading="uploading" @click="pickUpload">上传</el-button>
        <el-button
          :icon="CopyDocument"
          :disabled="!clipboard"
          @click="paste"
        >
          粘贴{{ clipboard ? `（${clipboard.sources.length}）` : '' }}
        </el-button>
        <el-button :icon="Refresh" @click="load(currentPath)">刷新</el-button>
      </div>

      <input ref="uploadInput" type="file" multiple class="hidden-input" @change="onUploadChange" />
    </div>

    <div class="path-bar">
      <el-button text :disabled="!parentPath" @click="parentPath && load(parentPath)">
        上一级
      </el-button>

      <el-breadcrumb separator="/" class="crumbs">
        <el-breadcrumb-item v-for="crumb in breadcrumbs" :key="crumb.path">
          <a class="crumb" @click.prevent="load(crumb.path)">{{ crumb.label }}</a>
        </el-breadcrumb-item>
      </el-breadcrumb>

      <el-button
        type="danger"
        plain
        size="small"
        :disabled="selectedCount === 0"
        @click="removeEntries(selection)"
      >
        删除选中（{{ selectedCount }}）
      </el-button>
    </div>

    <div class="table-wrap">
      <el-table
        v-loading="loading"
        :data="entries"
        height="100%"
        row-key="path"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="44" />
        <el-table-column prop="name" label="名称" min-width="260" sortable>
          <template #default="{ row }">
            <span
              class="entry"
              :class="{ openable: row.isDirectory || isTextFile(row.name) }"
              @click.stop="openEntry(row)"
            >
              <el-icon class="entry-icon" :class="{ folder: row.isDirectory }">
                <Folder v-if="row.isDirectory" />
                <Document v-else />
              </el-icon>
              <span class="entry-name">{{ row.name }}</span>
              <el-tag v-if="row.isSymlink" size="small" type="info" effect="plain">链接</el-tag>
            </span>
          </template>
        </el-table-column>
        <el-table-column prop="size" label="大小" width="110" sortable>
          <template #default="{ row }">{{ row.isDirectory ? '—' : formatSize(row.size) }}</template>
        </el-table-column>
        <el-table-column
          prop="modifiedAt"
          label="修改时间"
          width="190"
          sortable
          :sort-method="sortByModified"
        >
          <template #default="{ row }">{{ formatTime(row.modifiedAt) }}</template>
        </el-table-column>
        <el-table-column prop="mode" label="权限" width="120" />
        <el-table-column label="操作" width="96" fixed="right" align="center">
          <template #default="{ row }">
            <!-- 阻止冒泡，避免点菜单时触发名称单击打开 -->
            <div @click.stop>
              <el-dropdown
                trigger="click"
                @command="(command: RowAction) => onRowAction(command, row)"
              >
                <el-button size="small" :icon="MoreFilled">操作</el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item
                      command="open"
                      :disabled="!row.isDirectory && !isTextFile(row.name)"
                    >
                      打开
                    </el-dropdown-item>
                    <el-dropdown-item command="newtab" :disabled="!row.isDirectory">
                      在新页签打开
                    </el-dropdown-item>
                    <el-dropdown-item
                      command="edit"
                      :disabled="row.isDirectory || !isTextFile(row.name)"
                    >
                      编辑文本
                    </el-dropdown-item>
                    <el-dropdown-item command="download" :disabled="row.isDirectory">
                      下载
                    </el-dropdown-item>
                    <el-dropdown-item command="rename">重命名</el-dropdown-item>
                    <el-dropdown-item command="copy">复制</el-dropdown-item>
                    <el-dropdown-item command="cut">剪切</el-dropdown-item>
                    <el-dropdown-item command="delete" divided class="danger-item">
                      删除
                    </el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div class="status-bar">
      <span>共 {{ entries.length }} 项</span>
      <span v-if="clipboard" class="clipboard">
        剪贴板：{{ clipboard.mode === 'copy' ? '复制' : '剪切' }} {{ clipboard.sources.length }} 项
        <el-button text size="small" @click="filesClipboard.clear()">清空</el-button>
      </span>
      <span v-if="truncated" class="warning">目录条目过多，仅显示前 {{ entries.length }} 项</span>
    </div>

    <el-dialog v-model="editDialog" :title="editState.title" width="420px">
      <el-input
        v-model="editState.value"
        placeholder="请输入名称"
        maxlength="255"
        @keyup.enter="submitEdit"
      />
      <template #footer>
        <el-button @click="editDialog = false">取消</el-button>
        <el-button type="primary" :loading="editLoading" @click="submitEdit">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.file-pane {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 0 4px 12px;
}

.toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
  margin-bottom: 8px;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin-left: auto;
}

.path-bar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.crumbs {
  flex: 1;
  min-width: 0;
}

.crumb {
  color: #409eff;
  cursor: pointer;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  margin-top: 8px;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.entry {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  cursor: default;
}

.entry.openable {
  cursor: pointer;
}

.entry.openable:hover .entry-name {
  color: #409eff;
  text-decoration: underline;
}

.entry-icon {
  color: #909399;
}

.entry-icon.folder {
  color: #e6a23c;
}

.entry-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.status-bar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 16px;
  padding: 6px 2px 0;
  color: #909399;
  font-size: 12px;
}

.clipboard {
  display: flex;
  align-items: center;
  gap: 4px;
}

.warning {
  color: #e6a23c;
}

.hidden-input {
  display: none;
}

/* 下拉菜单传送到 body，scoped 样式无法命中，这里用全局选择器标记危险项 */
:global(.el-dropdown-menu__item.danger-item) {
  color: #f56c6c;
}
</style>
