<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Folder, Setting } from '@element-plus/icons-vue'
import {
  getFileRoots,
  getFileSettings,
  saveFileSettings,
  type FileSettings,
} from '@/api/files'
import { ApiError } from '@/api/http'
import FileBrowserPane from '@/components/files/FileBrowserPane.vue'
import { useAuthStore } from '@/stores/auth'
import { useFilesQuickAccessStore } from '@/stores/filesQuickAccess'
import {
  QUICK_ACCESS_DEFAULT_WIDTH,
  QUICK_ACCESS_MAX_WIDTH,
  QUICK_ACCESS_MIN_WIDTH,
  clampQuickAccessWidth,
  loadQuickAccessWidth,
  saveQuickAccessWidth,
} from '@/utils/filesLayout'

interface FileTab {
  id: string
  /** 页签标题随当前目录变化。 */
  title: string
  /** 页签创建时的目录，挂载后由面板自行导航。 */
  initialPath: string
  /** 页签当前目录，由面板的 path-change 事件同步，用于快速访问高亮。 */
  path: string
}

const tabs = ref<FileTab[]>([])
const activeKey = ref('')
const homePath = ref('')
const loadingRoots = ref(false)
/** 设置中关闭文件管理后显示的常驻提示。 */
const disabledNotice = ref(false)
let counter = 0

// ---------------- 左侧快速访问（按登录账号保存在 SQLite） ----------------
const quickAccess = useFilesQuickAccessStore()
const quickItems = computed(() => quickAccess.items)
/** 接口不可用（403/503）时静默隐藏侧栏，不打扰页面。 */
const quickAccessVisible = computed(() => !disabledNotice.value && quickAccess.loaded)
const quickSettingsVisible = ref(false)
const removingQuickId = ref<number | null>(null)
/** 当前激活页签的目录，用于高亮对应快速访问条目。 */
const activePath = computed(
  () => tabs.value.find((tab) => tab.id === activeKey.value)?.path ?? '',
)
/** 左侧条目点击产生的导航请求；seq 变化即触发当前激活页签跳转。 */
const navigateRequest = ref<{ path: string; seq: number } | null>(null)
let navigateSeq = 0

// ---------------- 快速访问栏宽度（可拖拽，按登录账号本地记忆） ----------------
const auth = useAuthStore()
const accountName = computed(() => auth.user?.userName ?? '')
const quickWidth = ref(QUICK_ACCESS_DEFAULT_WIDTH)
const resizing = ref(false)
const bodyRef = ref<HTMLElement | null>(null)
/** 内容区至少保留的宽度，避免侧栏把文件列表挤没。 */
const minContentWidth = 240
let resizeStartX = 0
let resizeStartWidth = 0
let resizePointerId: number | null = null
let previousBodyUserSelect = ''
let previousBodyCursor = ''

/** 按容器宽度计算侧栏可用的最大宽度。 */
function contentMaxWidth(): number {
  const total = bodyRef.value?.clientWidth ?? 0
  if (total <= 0) {
    return QUICK_ACCESS_MAX_WIDTH
  }

  return total - minContentWidth
}

/** 写入宽度前统一夹在 [160, 480] 且不超过容器限制。 */
function applyResizeWidth(width: number): void {
  quickWidth.value = clampQuickAccessWidth(width, contentMaxWidth())
}

/** 只在拖拽结束、双击或键盘调整时落盘，避免 pointermove 高频写入。 */
function persistQuickWidth(): void {
  saveQuickAccessWidth(accountName.value, quickWidth.value)
}

/** 恢复账号上次保存的宽度；无记录保持默认值。 */
function loadSavedQuickWidth(): void {
  const saved = loadQuickAccessWidth(accountName.value, contentMaxWidth())
  if (saved !== null) {
    quickWidth.value = saved
  }
}

function startResize(event: PointerEvent): void {
  if (event.button !== 0) {
    return
  }

  event.preventDefault()
  const handle = event.currentTarget as HTMLElement
  handle.setPointerCapture(event.pointerId)
  resizePointerId = event.pointerId
  resizeStartX = event.clientX
  resizeStartWidth = quickWidth.value
  resizing.value = true
  previousBodyUserSelect = document.body.style.userSelect
  previousBodyCursor = document.body.style.cursor
  document.body.style.userSelect = 'none'
  document.body.style.cursor = 'col-resize'
}

function onResizeMove(event: PointerEvent): void {
  if (!resizing.value || event.pointerId !== resizePointerId) {
    return
  }

  applyResizeWidth(resizeStartWidth + (event.clientX - resizeStartX))
}

/** 结束拖拽；pointerup / pointercancel / lostpointercapture 都会走到这里。 */
function finishResize(event: PointerEvent): void {
  if (!resizing.value || event.pointerId !== resizePointerId) {
    return
  }

  resizing.value = false
  resizePointerId = null
  releaseDragStyles()
  persistQuickWidth()
}

function releaseDragStyles(): void {
  document.body.style.userSelect = previousBodyUserSelect
  document.body.style.cursor = previousBodyCursor
  previousBodyUserSelect = ''
  previousBodyCursor = ''
}

/** 双击分隔条恢复默认宽度并保存。 */
function resetQuickWidth(): void {
  applyResizeWidth(QUICK_ACCESS_DEFAULT_WIDTH)
  persistQuickWidth()
}

/** 键盘可达：左右方向键每次调整 16px，并保存。 */
function onResizeKeydown(event: KeyboardEvent): void {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
    return
  }

  event.preventDefault()
  const delta = event.key === 'ArrowLeft' ? -16 : 16
  applyResizeWidth(quickWidth.value + delta)
  persistQuickWidth()
}

// ---------------- 文件管理设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<FileSettings | null>(null)
const rootSuggestions = ['/', '/home', '/srv', '/opt', '/var/log']
const settingsForm = reactive({
  enabled: true,
  allowedRoots: [] as string[],
  maxUploadMiB: 100,
  maxListEntries: 5000,
  maxTextMiB: 5,
  helperTimeoutSeconds: 120,
  listTimeoutSeconds: 300,
  transferTimeoutSeconds: 1800,
  streamIdleTimeoutSeconds: 120,
  streamMaxSeconds: 0,
})

/** 页签标题取目录名，根目录显示 /；重名目录允许出现同名页签。 */
function titleForPath(path: string): string {
  if (!path || path === '/') {
    return '/'
  }

  const parts = path.split('/').filter(Boolean)
  return parts[parts.length - 1] ?? '/'
}

/** 新建页签并激活；path 可来自家目录或目录行菜单【在新页签打开】。 */
function addTab(path: string): void {
  if (!path) {
    ElMessage.warning('尚未获取家目录，请稍后重试')
    return
  }

  counter += 1
  const id = `files-${counter}-${Date.now()}`
  tabs.value.push({ id, title: titleForPath(path), initialPath: path, path })
  activeKey.value = id
}

function updateTabTitle(id: string, path: string): void {
  const tab = tabs.value.find((item) => item.id === id)
  if (tab) {
    tab.title = titleForPath(path)
    tab.path = path
  }
}

/**
 * 点击左侧快速访问条目：把导航请求交给当前激活的内层页签。
 * 隐藏页签不会收到请求，因此其它页签的目录保持不变。
 */
function navigateQuick(path: string): void {
  if (!activeKey.value) {
    return
  }

  navigateSeq += 1
  navigateRequest.value = { path, seq: navigateSeq }
}

/** 从管理抽屉移除条目；成功后侧栏立即同步。 */
async function removeQuickItem(id: number): Promise<void> {
  removingQuickId.value = id
  try {
    await quickAccess.remove(id)
    ElMessage.success('已移除')
  } catch (error) {
    showError(error)
  } finally {
    removingQuickId.value = null
  }
}

/** 关闭页签：至少保留一个，关闭最后一个后回到家目录。 */
function removeTab(name: string | number): void {
  const key = String(name)
  const index = tabs.value.findIndex((tab) => tab.id === key)
  if (index === -1) {
    return
  }

  const wasActive = activeKey.value === key
  tabs.value.splice(index, 1)

  if (wasActive) {
    const next = tabs.value[index] ?? tabs.value[index - 1] ?? null
    activeKey.value = next?.id ?? ''
  }

  if (tabs.value.length === 0) {
    addTab(homePath.value)
  }
}

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

function bytesToMiB(bytes: number): number {
  return Math.max(1, Math.round(bytes / (1024 * 1024)))
}

function applySettingsForm(value: FileSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.allowedRoots = [...value.allowedRoots]
  settingsForm.maxUploadMiB = bytesToMiB(value.maxUploadBytes)
  settingsForm.maxListEntries = value.maxListEntries
  settingsForm.maxTextMiB = bytesToMiB(value.maxTextFileBytes)
  settingsForm.helperTimeoutSeconds = value.helperTimeoutSeconds
  settingsForm.listTimeoutSeconds = value.listTimeoutSeconds
  settingsForm.transferTimeoutSeconds = value.transferTimeoutSeconds
  settingsForm.streamIdleTimeoutSeconds = value.streamIdleTimeoutSeconds
  settingsForm.streamMaxSeconds = value.streamMaxSeconds
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getFileSettings()
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

/** 保存后静默刷新家目录；关闭文件管理时该请求会 503，保持当前页面状态即可。 */
async function refreshRootsQuietly(): Promise<void> {
  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
  } catch {
    // 配置已保存，根目录刷新失败不影响抽屉结果提示。
  }
}

async function saveSettings(): Promise<void> {
  settingsSaving.value = true
  try {
    const saved = await saveFileSettings({
      enabled: settingsForm.enabled,
      allowedRoots: settingsForm.allowedRoots
        .map((root) => root.trim())
        .filter((root) => root.length > 0),
      maxUploadBytes: Math.round(settingsForm.maxUploadMiB * 1024 * 1024),
      maxListEntries: settingsForm.maxListEntries,
      maxTextFileBytes: Math.round(settingsForm.maxTextMiB * 1024 * 1024),
      helperTimeoutSeconds: settingsForm.helperTimeoutSeconds,
      listTimeoutSeconds: settingsForm.listTimeoutSeconds,
      transferTimeoutSeconds: settingsForm.transferTimeoutSeconds,
      streamIdleTimeoutSeconds: settingsForm.streamIdleTimeoutSeconds,
      streamMaxSeconds: settingsForm.streamMaxSeconds,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    disabledNotice.value = !saved.enabled
    ElMessage.success('设置已保存并立即生效')

    // 重新启用文件管理后刷新快速访问列表（关闭期间接口返回 503，侧栏保持隐藏）。
    if (saved.enabled) {
      void quickAccess.load(true)
    }

    // 从关闭状态重新启用且页面还没有页签时，重新建立首个家目录页签。
    if (saved.enabled && tabs.value.length === 0) {
      await init()
    } else {
      await refreshRootsQuietly()
    }
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

/** 首次进入时读取家目录并创建首个页签。 */
async function init(): Promise<void> {
  loadingRoots.value = true
  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
    addTab(roots.home)
  } catch (error) {
    // 管理员在设置中关闭文件管理（或平台不支持）时返回 503，用常驻提示而不是临时 toast。
    if (error instanceof ApiError && error.status === 503) {
      disabledNotice.value = true
      return
    }

    showError(error)
  } finally {
    loadingRoots.value = false
  }
}

onMounted(() => {
  loadSavedQuickWidth()
  void init()
  void loadSettingsAvailability()
  // 快速访问对每个登录账号独立，失败（403/503）时静默隐藏侧栏；重新打开面板时顺便刷新。
  void quickAccess.load(true)
})

// 会话恢复较晚时账号名可能稍后才可用，补读一次该账号的宽度。
watch(accountName, (name) => {
  if (name) {
    loadSavedQuickWidth()
  }
})

// 拖拽期间侧栏若被隐藏（例如接口失败），分隔条会卸载而收不到 pointerup，这里兜底复位。
watch(quickAccessVisible, (visible) => {
  if (!visible && resizing.value) {
    resizing.value = false
    resizePointerId = null
    releaseDragStyles()
    persistQuickWidth()
  }
})

onBeforeUnmount(() => {
  resizing.value = false
  resizePointerId = null
  releaseDragStyles()
})
</script>

<template>
  <div class="files-view">
    <div class="toolbar">
      <span class="title">文件管理</span>
      <span class="hint">每个页签独立浏览目录；复制或剪切后切换到其它页签即可直接粘贴</span>
      <el-tooltip v-if="settingsAvailable" content="文件管理设置" placement="bottom">
        <el-button
          class="settings-button"
          :icon="Setting"
          circle
          :loading="settingsLoading"
          aria-label="文件管理设置"
          @click="openSettings"
        />
      </el-tooltip>
    </div>

    <el-alert
      v-if="disabledNotice"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="文件管理不可用"
      description="文件管理可能已被管理员关闭，或当前平台不支持；相关操作会返回 503，重新启用后即可继续使用。"
    />

    <div ref="bodyRef" class="body">
      <!-- 左侧快速访问：按登录账号保存在 SQLite，顶部独立设置按钮用于移除条目 -->
      <aside
        v-if="quickAccessVisible"
        class="quick-access"
        :style="{ width: `${quickWidth}px` }"
      >
        <div class="quick-access-header">
          <span class="quick-access-title">快速访问</span>
          <el-tooltip content="快速访问设置" placement="bottom">
            <el-button
              class="quick-settings"
              :icon="Setting"
              circle
              size="small"
              aria-label="快速访问设置"
              @click="quickSettingsVisible = true"
            />
          </el-tooltip>
        </div>
        <div class="quick-access-list">
          <el-empty
            v-if="quickItems.length === 0"
            :image-size="48"
            description="暂无快速访问目录"
          />
          <button
            v-for="item in quickItems"
            :key="item.id"
            type="button"
            class="quick-item"
            :class="{ active: item.path === activePath }"
            :title="item.path"
            @click="navigateQuick(item.path)"
          >
            <el-icon class="quick-item-icon"><Folder /></el-icon>
            <span class="quick-item-name">{{ item.name }}</span>
          </button>
        </div>
      </aside>

      <!-- 拖拽分隔条：松手后按当前登录账号保存宽度，双击恢复默认值 -->
      <div
        v-if="quickAccessVisible"
        class="resizer"
        :class="{ resizing }"
        role="separator"
        aria-orientation="vertical"
        aria-label="调整快速访问栏宽度"
        :aria-valuenow="quickWidth"
        :aria-valuemin="QUICK_ACCESS_MIN_WIDTH"
        :aria-valuemax="QUICK_ACCESS_MAX_WIDTH"
        tabindex="0"
        title="拖动调整宽度，双击恢复默认"
        @pointerdown="startResize"
        @pointermove="onResizeMove"
        @pointerup="finishResize"
        @pointercancel="finishResize"
        @lostpointercapture="finishResize"
        @dblclick="resetQuickWidth"
        @keydown="onResizeKeydown"
      />

      <div class="content">
        <!-- 页签面板保持挂载、仅切换显隐，目录位置与选中项随页签保留 -->
        <el-tabs
          v-if="tabs.length > 0"
          v-model="activeKey"
          type="card"
          editable
          class="tabs"
          @tab-add="addTab(homePath)"
          @tab-remove="removeTab"
        >
          <el-tab-pane
            v-for="tab in tabs"
            :key="tab.id"
            :label="tab.title"
            :name="tab.id"
            :closable="tabs.length > 1"
          >
            <FileBrowserPane
              :initial-path="tab.initialPath"
              :home-path="homePath"
              :navigate-request="tab.id === activeKey ? navigateRequest : null"
              @path-change="updateTabTitle(tab.id, $event)"
              @open-in-new-tab="addTab"
              @navigate-consumed="navigateRequest = null"
            />
          </el-tab-pane>
        </el-tabs>

        <div v-else v-loading="loadingRoots" class="empty">
          <el-empty :description="loadingRoots ? '正在获取家目录…' : '无法获取家目录，请重试'">
            <el-button v-if="!loadingRoots" type="primary" @click="init">重试</el-button>
          </el-empty>
        </div>
      </div>
    </div>

    <el-drawer
      v-model="quickSettingsVisible"
      title="快速访问设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        快速访问列表保存在 SQLite 数据库中，按登录账号独立保存，仅影响当前账号；这里可以移除不再需要的目录。
      </p>
      <el-empty v-if="quickItems.length === 0" description="暂无快速访问目录" />
      <ul v-else class="quick-manage-list">
        <li v-for="item in quickItems" :key="item.id" class="quick-manage-item">
          <div class="quick-manage-info">
            <div class="quick-manage-name">{{ item.name }}</div>
            <div class="quick-manage-path">{{ item.path }}</div>
          </div>
          <el-button
            link
            type="danger"
            :loading="removingQuickId === item.id"
            @click="removeQuickItem(item.id)"
          >
            移除
          </el-button>
        </li>
      </ul>
      <div class="form-hint">
        在文件列表目录行的「操作」菜单中选择【添加到快速访问】即可添加；目录不存在或不可访问时打开会提示错误。
      </div>
    </el-drawer>

    <el-drawer
      v-model="settingsVisible"
      title="文件管理设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 files），保存后立即生效，无需重启服务。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用文件管理">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后所有账号（含 root）的文件操作接口都返回 503。</div>
        </el-form-item>
        <el-form-item label="允许访问的根目录">
          <el-select
            v-model="settingsForm.allowedRoots"
            class="full-width"
            multiple
            filterable
            allow-create
            default-first-option
            placeholder="输入绝对路径后按回车添加"
          >
            <el-option
              v-for="root in rootSuggestions"
              :key="root"
              :label="root"
              :value="root"
            />
          </el-select>
          <div class="form-hint">
            默认 / 表示整个文件系统；必须为绝对路径，越权仍由登录用户的系统权限兜底。
          </div>
        </el-form-item>

        <el-divider content-position="left">上限</el-divider>
        <el-form-item label="单个上传文件（MiB）">
          <el-input-number
            v-model="settingsForm.maxUploadMiB"
            class="full-width"
            :min="1"
            :max="512"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="目录列举条目上限">
          <el-input-number
            v-model="settingsForm.maxListEntries"
            class="full-width"
            :min="1"
            :max="100000"
            :step="100"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="在线编辑文本上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxTextMiB"
            class="full-width"
            :min="1"
            :max="64"
            controls-position="right"
          />
          <div class="form-hint">读取与保存在线编辑内容都受此限制，过大会拖慢浏览器。</div>
        </el-form-item>

        <el-divider content-position="left">超时</el-divider>
        <el-form-item label="普通操作超时（秒）">
          <el-input-number
            v-model="settingsForm.helperTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
          <div class="form-hint">新建目录、新建文件、重命名等元数据操作。</div>
        </el-form-item>
        <el-form-item label="目录列举超时（秒）">
          <el-input-number
            v-model="settingsForm.listTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="删除/复制/移动超时（秒）">
          <el-input-number
            v-model="settingsForm.transferTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="86400"
            :step="60"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="上传/下载空闲超时（秒）">
          <el-input-number
            v-model="settingsForm.streamIdleTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
          <div class="form-hint">没有任何数据流动超过该时间即终止操作。</div>
        </el-form-item>
        <el-form-item label="上传/下载总时长上限（秒）">
          <el-input-number
            v-model="settingsForm.streamMaxSeconds"
            class="full-width"
            :min="0"
            :max="86400"
            :step="60"
            controls-position="right"
          />
          <div class="form-hint">0 表示不限制，仅依靠空闲超时兜底。</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="settingsVisible = false">取消</el-button>
        <el-button type="primary" :loading="settingsSaving" @click="saveSettings">保存</el-button>
      </template>
    </el-drawer>
  </div>
</template>

<style scoped>
.files-view {
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

.hint {
  margin-left: auto;
  color: #909399;
  font-size: 12px;
}

.settings-button {
  flex: none;
  margin-left: 2px;
}

.notice {
  flex: none;
  margin-bottom: 8px;
}

.body {
  display: flex;
  flex: 1;
  gap: 0;
  min-height: 0;
}

/* 左侧快速访问栏；宽度由拖拽分隔条控制并按账号记忆，设置按钮独立于右上角的文件管理设置齿轮 */
.quick-access {
  display: flex;
  flex: none;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

/* 分隔条同时承担侧栏与内容区的间距；竖条只做视觉提示，热区为整条 9px */
.resizer {
  position: relative;
  flex: none;
  align-self: stretch;
  width: 9px;
  min-height: 0;
  border-radius: 4px;
  cursor: col-resize;
  touch-action: none;
  outline: none;
}

.resizer::before {
  content: '';
  position: absolute;
  top: 50%;
  left: 50%;
  width: 3px;
  height: 40px;
  border-radius: 2px;
  background: #dcdfe6;
  transform: translate(-50%, -50%);
  transition: background-color 0.15s;
}

.resizer:hover::before,
.resizer.resizing::before,
.resizer:focus-visible::before {
  background: #409eff;
}

.resizer:focus-visible {
  outline: 2px solid #409eff;
  outline-offset: -2px;
}

.quick-access-header {
  display: flex;
  flex: none;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-bottom: 1px solid #ebeef5;
}

.quick-access-title {
  flex: 1;
  color: #303133;
  font-size: 13px;
  font-weight: 600;
}

.quick-settings {
  flex: none;
}

.quick-access-list {
  flex: 1;
  min-height: 0;
  padding: 4px;
  overflow-y: auto;
}

.quick-item {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: #303133;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.quick-item:hover {
  background: #f5f7fa;
  color: #409eff;
}

.quick-item.active {
  background: #ecf5ff;
  color: #409eff;
}

.quick-item-icon {
  flex: none;
  color: #e6a23c;
}

.quick-item-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.content {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.quick-manage-list {
  margin: 0;
  padding: 0;
  list-style: none;
}

.quick-manage-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 4px;
  border-bottom: 1px solid #ebeef5;
}

.quick-manage-info {
  flex: 1;
  min-width: 0;
}

.quick-manage-name {
  color: #303133;
  font-weight: 600;
}

.quick-manage-path {
  color: #8a919f;
  font-size: 12px;
  word-break: break-all;
}

.tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.tabs :deep(.el-tabs__header) {
  flex: none;
  margin: 0 0 8px;
}

.tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.tabs :deep(.el-tab-pane) {
  height: 100%;
}

.empty {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  min-height: 0;
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

.settings-form :deep(.el-divider) {
  margin: 8px 0 16px;
}

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}

.full-width {
  width: 100%;
}
</style>
