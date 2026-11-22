<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox, type InputInstance } from 'element-plus'
import {
  CopyDocument,
  Document,
  DocumentAdd,
  Folder,
  FolderAdd,
  Monitor,
  MoreFilled,
  Position,
  Refresh,
  Upload,
} from '@element-plus/icons-vue'
import type {
  FileEntry,
  FileMutationResponse,
  FileTransferPhase,
  FileTransferProgress,
  UploadProgress,
} from '@/api/files'
import {
  buildDownloadUrl,
  cancelFileTransfer,
  copyEntries,
  createFile,
  deleteEntries,
  getFileTransferProgress,
  listFiles,
  makeDirectory,
  moveEntries,
  renameEntry,
  uploadFile,
} from '@/api/files'
import { ApiError } from '@/api/http'
import { isTextFile } from '@/config/appConfig'
import { isImageFile } from '@/config/imageTypes'
import FileSidebar from '@/components/files/FileSidebar.vue'
import { useEditorStore } from '@/stores/editor'
import { type FileClipboard, useFilesClipboardStore } from '@/stores/filesClipboard'
import { useFilesQuickAccessStore } from '@/stores/filesQuickAccess'
import { useImageBrowserStore } from '@/stores/imageBrowser'
import { useTerminalStore } from '@/stores/terminal'
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
  /** 登录用户的家目录，用于把跳转输入框中的 ~ 展开为绝对路径。 */
  homePath: string
  /** 允许访问的根目录，作为本页签侧栏目录树的根节点。 */
  roots: string[]
  /** 文件管理被管理员关闭时隐藏侧栏，文件列表仍保持当前状态。 */
  sidebarVisible: boolean
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
const filesQuickAccess = useFilesQuickAccessStore()
const imageBrowser = useImageBrowserStore()
const terminal = useTerminalStore()

const currentPath = ref('')
const parentPath = ref<string | null>(null)
const entries = ref<FileEntry[]>([])
const truncated = ref(false)
const loading = ref(false)
const uploading = ref(false)
/** 上传进度：当前文件的百分比与序号，只在 uploading 为 true 时渲染进度行。 */
const uploadProgress = ref(0)
const uploadFileName = ref('')
const uploadIndex = ref(0)
const uploadTotal = ref(0)
const selection = ref<FileEntry[]>([])
const uploadInput = ref<HTMLInputElement | null>(null)

/** 粘贴进度：复制/移动期间显示内联进度行，POST 返回后立即隐藏。 */
const transferActive = ref(false)
const transferMode = ref<'copy' | 'move'>('copy')
const transferPhase = ref<FileTransferPhase>('scanning')
const transferBytesDone = ref(0)
const transferBytesTotal = ref(0)
const transferEntriesDone = ref(0)
const transferEntriesTotal = ref(0)
const transferCurrentName = ref('')
const transferCancelLoading = ref(false)
let transferOperationId = ''
let transferTimer: number | undefined

/** 剪贴板来自共享 store，因此在其它文件页签复制的条目本页签也能直接粘贴。 */
const clipboard = computed(() => filesClipboard.clipboard)

const editDialog = ref(false)
const editLoading = ref(false)
const editState = ref<EditState>({ mode: 'mkdir', title: '', value: '' })

// ---------------- 路径跳转 ----------------
const jumpDialog = ref(false)
const jumpValue = ref('')
const jumpLoading = ref(false)
const jumpInput = ref<InputInstance | null>(null)

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

/** 已完成比例：优先按字节，纯目录/符号链接场景回退条目数。 */
const transferPercent = computed(() => {
  if (transferBytesTotal.value > 0) {
    return Math.min(100, Math.round((transferBytesDone.value / transferBytesTotal.value) * 100))
  }

  if (transferEntriesTotal.value > 0) {
    return Math.min(100, Math.round((transferEntriesDone.value / transferEntriesTotal.value) * 100))
  }

  return 0
})

/** 统计阶段或总数未知时显示不确定进度条，避免误导为 0%。 */
const transferIndeterminate = computed(
  () =>
    transferPhase.value === 'scanning' ||
    (transferBytesTotal.value === 0 && transferEntriesTotal.value === 0),
)

const transferLabel = computed(() => {
  const action = transferMode.value === 'move' ? '剪切粘贴' : '粘贴'
  if (transferPhase.value === 'scanning') {
    return `正在统计待${action}的内容（已扫描 ${transferEntriesDone.value} 项）`
  }

  const progress =
    transferBytesTotal.value > 0
      ? `${formatSize(transferBytesDone.value)} / ${formatSize(transferBytesTotal.value)}`
      : `${transferEntriesDone.value} / ${transferEntriesTotal.value} 项`
  const current = transferCurrentName.value ? ` ${transferCurrentName.value}` : ''
  return `正在${action}（${progress}）${current}`
})

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

/** 返回是否加载成功，供跳转对话框决定要不要关闭。 */
async function load(path?: string): Promise<boolean> {
  loading.value = true
  try {
    const data = await listFiles(path)
    currentPath.value = data.path
    parentPath.value = data.parent
    entries.value = data.entries
    truncated.value = data.truncated
    selection.value = []
    emit('path-change', data.path)
    return true
  } catch (error) {
    showError(error)
    return false
  } finally {
    loading.value = false
  }
}

/**
 * 把跳转输入解析为绝对路径：
 * `/` 开头原样使用，`~` 与 `~/...` 展开为家目录，其余按当前目录的相对路径拼接。
 * 存在性、目录类型、允许根与权限仍由后端校验。
 */
function resolveJumpPath(raw: string): string | null {
  const value = raw.trim()
  if (!value) {
    ElMessage.warning('路径不能为空')
    return null
  }

  if (value === '~' || value.startsWith('~/')) {
    if (!props.homePath) {
      ElMessage.warning('尚未获取家目录，请稍后重试')
      return null
    }

    const rest = value.slice(1).replace(/^\/+/, '')
    return rest ? joinPath(props.homePath, rest) : props.homePath
  }

  if (value.startsWith('/')) {
    return value
  }

  return joinPath(currentPath.value || '/', value)
}

function openJump(): void {
  jumpValue.value = currentPath.value
  jumpDialog.value = true
  void nextTick(() => jumpInput.value?.focus())
}

async function submitJump(): Promise<void> {
  if (jumpLoading.value) {
    return
  }

  const target = resolveJumpPath(jumpValue.value)
  if (!target) {
    return
  }

  jumpLoading.value = true
  try {
    if (await load(target)) {
      jumpDialog.value = false
    }
  } finally {
    jumpLoading.value = false
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

/**
 * 以指定目录为初始工作目录新建终端页签：请求经终端 store 交给 TerminalView，
 * 再切换到工作区「终端」标签（首次打开会挂载终端面板并消费该请求）。
 */
function openTerminal(directory: string): void {
  if (!directory) {
    return
  }

  terminal.requestTab(directory)
  workspace.open('terminal')
}

/** 仅 `.sh` 脚本（不区分大小写）提供「执行」。 */
function isShellScript(name: string): boolean {
  return name.toLowerCase().endsWith('.sh')
}

/** POSIX 单引号转义：路径里的引号、空格等字符不会被 Shell 解释为附加命令。 */
function shellQuote(value: string): string {
  return `'${value.replace(/'/g, `'\\''`)}'`
}

/**
 * 执行 .sh 脚本：新建终端页签，初始工作目录为脚本所在目录，用 sh 执行脚本。
 * `exec` 让脚本进程直接成为 PTY 子进程，脚本结束后 sh 退出、服务端关闭 WebSocket，
 * 页签保留并显示「终端进程已退出」（不会重新回到交互式 Shell）。
 */
function runScript(row: FileEntry): void {
  if (row.isDirectory || !isShellScript(row.name)) {
    return
  }

  // 换行等控制字符会截断终端行输入，且不应出现在可执行路径中。
  if (/[\u0000-\u001f\u007f]/.test(row.path)) {
    ElMessage.warning('脚本路径包含控制字符，无法执行')
    return
  }

  const slash = row.path.lastIndexOf('/')
  const directory = slash > 0 ? row.path.slice(0, slash) : '/'
  terminal.requestTab(directory, `exec sh ${shellQuote(row.path)}`)
  workspace.open('terminal')
}

/** 把目录加入当前账号的快速访问列表；重复添加只提示不报错。 */
async function addQuickAccess(row: FileEntry): Promise<void> {
  if (!row.isDirectory) {
    return
  }

  try {
    const created = await filesQuickAccess.add(row.path)
    if (created) {
      ElMessage.success('已添加到快速访问')
    } else {
      ElMessage.info('该目录已在快速访问列表中')
    }
  } catch (error) {
    showError(error)
  }
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
type RowAction =
  | 'open'
  | 'newtab'
  | 'terminal'
  | 'quick'
  | 'edit'
  | 'images'
  | 'run'
  | 'download'
  | 'rename'
  | 'copy'
  | 'cut'
  | 'delete'

/**
 * 打开图片浏览：目录行加载该目录（含子目录）；图片文件行加载父目录并定位到该图片。
 * 请求经 imageBrowser store 交给 ImageView，再切换到工作区「图片浏览」标签。
 */
function browseImages(row: FileEntry): void {
  if (row.isDirectory) {
    imageBrowser.requestDirectory(row.path)
  } else if (isImageFile(row.name)) {
    const slash = row.path.lastIndexOf('/')
    const directory = slash > 0 ? row.path.slice(0, slash) : '/'
    imageBrowser.requestDirectory(directory, row.path)
  } else {
    return
  }

  workspace.open('image')
}

function onRowAction(command: RowAction, row: FileEntry): void {
  switch (command) {
    case 'open':
      openEntry(row)
      break
    case 'newtab':
      openInNewTab(row)
      break
    case 'terminal':
      if (row.isDirectory) {
        openTerminal(row.path)
      }
      break
    case 'quick':
      void addQuickAccess(row)
      break
    case 'edit':
      openTextEditor(row)
      break
    case 'images':
      browseImages(row)
      break
    case 'run':
      runScript(row)
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

/**
 * 生成客户端操作标识：优先 `crypto.randomUUID()`（安全上下文），
 * 否则回退到随机字节的十六进制串，满足后端的 `^[A-Za-z0-9-]{8,64}$`。
 */
function newTransferId(): string {
  const cryptoApi = window.crypto
  if (typeof cryptoApi?.randomUUID === 'function') {
    return cryptoApi.randomUUID()
  }

  const bytes = new Uint8Array(16)
  if (typeof cryptoApi?.getRandomValues === 'function') {
    cryptoApi.getRandomValues(bytes)
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256)
    }
  }

  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
}

function stopTransferPolling(): void {
  if (transferTimer !== undefined) {
    window.clearInterval(transferTimer)
    transferTimer = undefined
  }
}

function applyTransferProgress(progress: FileTransferProgress): void {
  transferPhase.value = progress.phase
  transferBytesDone.value = progress.bytesDone
  transferBytesTotal.value = progress.bytesTotal
  transferEntriesDone.value = progress.entriesDone
  transferEntriesTotal.value = progress.entriesTotal
  transferCurrentName.value = progress.currentName ?? ''
}

/** 轮询期间的操作标识可能已切换；只应用当前操作的结果，丢弃过期响应。 */
async function refreshTransferProgress(): Promise<void> {
  const operationId = transferOperationId
  if (!transferActive.value || !operationId) {
    return
  }

  try {
    const progress = await getFileTransferProgress(operationId)
    if (!transferActive.value || transferOperationId !== operationId) {
      return
    }

    applyTransferProgress(progress)
  } catch {
    // 首次轮询可能早于后端登记操作而得到 404，也可能是 403/503。
    // 这里保持轮询直到 POST 返回（endTransfer 停止）：最终结果与错误由 POST 决定。
  }
}

function beginTransfer(mode: 'copy' | 'move', operationId: string): void {
  transferOperationId = operationId
  transferMode.value = mode
  transferActive.value = true
  transferCancelLoading.value = false
  transferPhase.value = 'scanning'
  transferBytesDone.value = 0
  transferBytesTotal.value = 0
  transferEntriesDone.value = 0
  transferEntriesTotal.value = 0
  transferCurrentName.value = ''

  stopTransferPolling()
  void refreshTransferProgress()
  transferTimer = window.setInterval(() => {
    void refreshTransferProgress()
  }, 500)
}

function endTransfer(): void {
  stopTransferPolling()
  transferActive.value = false
  transferCancelLoading.value = false
  transferOperationId = ''
  transferCurrentName.value = ''
}

/** 发起一次复制/移动并全程显示进度；取消时后端返回 ok=false、cancelled=true。 */
async function startTransfer(
  clipboardValue: FileClipboard,
  overwrite: boolean,
): Promise<FileMutationResponse> {
  const operationId = newTransferId()
  const mode = clipboardValue.mode === 'cut' ? 'move' : 'copy'
  beginTransfer(mode, operationId)

  try {
    return mode === 'move'
      ? await moveEntries(clipboardValue.sources, currentPath.value, overwrite, operationId)
      : await copyEntries(clipboardValue.sources, currentPath.value, overwrite, operationId)
  } finally {
    endTransfer()
  }
}

/** 取消当前粘贴；实际停止发生在后端终止辅助进程之后。 */
async function cancelPaste(): Promise<void> {
  const operationId = transferOperationId
  if (!operationId || transferCancelLoading.value) {
    return
  }

  transferCancelLoading.value = true
  try {
    await cancelFileTransfer(operationId)
  } catch (error) {
    showError(error)
    transferCancelLoading.value = false
  }
}

/** 粘贴到当前页签所在目录；来源可以是在其它页签中复制的条目。 */
async function paste(): Promise<void> {
  const clipboardValue = filesClipboard.clipboard
  if (!clipboardValue || transferActive.value) {
    return
  }

  let result: FileMutationResponse | undefined
  try {
    result = await startTransfer(clipboardValue, false)
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
        result = await startTransfer(clipboardValue, true)
      } catch (retryError) {
        showError(retryError)
        return
      }
    } else {
      showError(error)
      return
    }
  }

  if (!result) {
    return
  }

  if (result.cancelled) {
    ElMessage.info('粘贴已取消，已复制的条目会保留')
    await load(currentPath.value)
    return
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

/** 开始上传某个文件：记录序号与文件名，并把进度归零。 */
function beginUploadFile(file: File, index: number): void {
  uploadFileName.value = file.name
  uploadIndex.value = index
  uploadProgress.value = 0
}

function setUploadProgress(progress: UploadProgress): void {
  uploadProgress.value = Math.min(100, Math.max(0, progress.percent))
}

async function onUploadChange(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (files.length === 0) {
    return
  }

  uploading.value = true
  uploadTotal.value = files.length
  let succeeded = 0
  try {
    for (const [index, file] of files.entries()) {
      beginUploadFile(file, index + 1)
      try {
        await uploadFile(currentPath.value, file, false, setUploadProgress)
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

        beginUploadFile(file, index + 1)
        try {
          await uploadFile(currentPath.value, file, true, setUploadProgress)
          succeeded += 1
        } catch (retryError) {
          showError(retryError)
        }
      }
    }
  } finally {
    uploading.value = false
    uploadProgress.value = 0
    uploadFileName.value = ''
    uploadIndex.value = 0
    uploadTotal.value = 0
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

/** 本页签侧栏的跳转请求直接作用于本页签，隐藏页签保持自己的目录。 */
function navigateTo(path: string): void {
  void load(path)
}

onMounted(() => {
  void load(props.initialPath)
})

onBeforeUnmount(() => {
  stopTransferPolling()
  if (transferActive.value && transferOperationId) {
    const operationId = transferOperationId
    transferActive.value = false
    // 关闭文件页签时尽力取消，避免复制在后台无人可见地继续。
    void cancelFileTransfer(operationId).catch(() => {})
  }
})
</script>

<template>
  <div class="file-pane">
    <div class="pane-body">
      <!-- 每个文件页签渲染自己的侧栏：快速访问与目录树状态随页签独立 -->
      <FileSidebar
        v-if="sidebarVisible"
        :roots="roots"
        :home-path="homePath"
        :current-path="currentPath"
        @navigate="navigateTo"
      />

      <div class="pane-content">
        <div class="toolbar">
          <div class="actions">
            <el-button :icon="FolderAdd" @click="openCreate('mkdir')">新建文件夹</el-button>
            <el-button :icon="DocumentAdd" @click="openCreate('touch')">新建文件</el-button>
            <el-button :icon="Upload" :loading="uploading" @click="pickUpload">上传</el-button>
            <el-button
              :icon="CopyDocument"
              :disabled="!clipboard"
              :loading="transferActive"
              @click="paste"
            >
              粘贴{{ clipboard ? `（${clipboard.sources.length}）` : '' }}
            </el-button>
            <el-button :icon="Refresh" @click="load(currentPath)">刷新</el-button>
          </div>

          <input ref="uploadInput" type="file" multiple class="hidden-input" @change="onUploadChange" />
        </div>

        <!-- 上传进度：当前文件的百分比与序号，结束后自动隐藏 -->
        <div v-if="uploading" class="upload-progress" role="status">
          <span class="upload-progress-label" :title="uploadFileName">
            正在上传（{{ uploadIndex }}/{{ uploadTotal }}）{{ uploadFileName }}
          </span>
          <el-progress
            class="upload-progress-bar"
            :percentage="uploadProgress"
            :stroke-width="6"
          />
        </div>

        <!-- 粘贴进度：统计阶段为不确定进度，复制阶段显示真实字节百分比与当前文件 -->
        <div v-if="transferActive" class="upload-progress" role="status" aria-live="polite">
          <span class="upload-progress-label" :title="transferLabel">{{ transferLabel }}</span>
          <el-progress
            class="upload-progress-bar"
            :percentage="transferPercent"
            :indeterminate="transferIndeterminate"
            :stroke-width="6"
          />
          <el-button size="small" :loading="transferCancelLoading" @click="cancelPaste">
            取消
          </el-button>
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

          <el-button size="small" :icon="Monitor" :disabled="!currentPath" @click="openTerminal(currentPath)">
            打开终端
          </el-button>

          <el-button size="small" :icon="Position" @click="openJump">跳转</el-button>

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
                        <el-dropdown-item command="terminal" :disabled="!row.isDirectory">
                          打开终端
                        </el-dropdown-item>
                        <el-dropdown-item command="quick" :disabled="!row.isDirectory">
                          添加到快速访问
                        </el-dropdown-item>
                        <el-dropdown-item
                          command="edit"
                          :disabled="row.isDirectory || !isTextFile(row.name)"
                        >
                          编辑文本
                        </el-dropdown-item>
                        <el-dropdown-item
                          command="images"
                          :disabled="!row.isDirectory && !isImageFile(row.name)"
                        >
                          浏览图片
                        </el-dropdown-item>
                        <el-dropdown-item
                          v-if="!row.isDirectory && isShellScript(row.name)"
                          command="run"
                        >
                          执行
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
      </div>
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

    <el-dialog v-model="jumpDialog" title="跳转到目录" width="480px">
      <el-input
        ref="jumpInput"
        v-model="jumpValue"
        clearable
        placeholder="输入目标目录，如 /var/log、logs 或 ~"
        @keyup.enter="submitJump"
      />
      <div class="jump-hint">
        支持 / 开头的绝对路径、相对当前目录的路径，以及 ~（家目录）与 ~/子目录；
        可访问范围仍由登录用户的系统权限决定。
      </div>
      <template #footer>
        <el-button @click="jumpDialog = false">取消</el-button>
        <el-button type="primary" :loading="jumpLoading" @click="submitJump">跳转</el-button>
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

/* 侧栏与文件内容并排；侧栏由 FileSidebar 自行控制宽度与分隔条 */
.pane-body {
  display: flex;
  flex: 1;
  min-height: 0;
}

.pane-content {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
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

/* 上传进度行：占工具栏下方一行，文件名过长时省略，进度条自适应 */
.upload-progress {
  display: flex;
  flex: none;
  align-items: center;
  gap: 12px;
  padding: 0 10px 8px;
  color: #606266;
  font-size: 12px;
}

.upload-progress-label {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.upload-progress-bar {
  flex: 1;
  min-width: 160px;
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

.jump-hint {
  margin-top: 8px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}

/* 下拉菜单传送到 body，scoped 样式无法命中，这里用全局选择器标记危险项 */
:global(.el-dropdown-menu__item.danger-item) {
  color: #f56c6c;
}
</style>
