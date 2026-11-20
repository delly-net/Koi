<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Folder, Setting } from '@element-plus/icons-vue'
import DirectoryTree from '@/components/files/DirectoryTree.vue'
import { ApiError } from '@/api/http'
import { useAuthStore } from '@/stores/auth'
import { useFilesQuickAccessStore } from '@/stores/filesQuickAccess'
import { useFilesSidebarLayoutStore } from '@/stores/filesSidebarLayout'
import {
  QUICK_ACCESS_DEFAULT_HEIGHT,
  QUICK_ACCESS_DEFAULT_WIDTH,
  QUICK_ACCESS_MAX_WIDTH,
  QUICK_ACCESS_MIN_HEIGHT,
  QUICK_ACCESS_MIN_WIDTH,
} from '@/utils/filesLayout'

/**
 * 单个文件页签的左侧栏：快速访问列表 + 目录树。
 *
 * 每个文件页签（FileBrowserPane）都渲染一份本组件，因此快速访问高亮、
 * 目录树展开状态、设置抽屉与滚动位置都随页签独立；列表数据仍来自共享的
 * filesQuickAccess store，任一页签新增/移除后所有页签立即同步。
 * 宽高是账号级共享偏好，由 filesSidebarLayout store 统一保存。
 */
const props = defineProps<{
  /** 允许访问的根目录，作为目录树的根节点。 */
  roots: string[]
  /** 登录账号家目录，用于树根兜底。 */
  homePath: string
  /** 所属文件页签的当前目录，用于快速访问高亮与目录树定位。 */
  currentPath: string
}>()

const emit = defineEmits<{
  /** 点击快速访问条目或目录树节点，请求所属文件页签跳转。 */
  (e: 'navigate', path: string): void
}>()

const auth = useAuthStore()
const quickAccess = useFilesQuickAccessStore()
const layout = useFilesSidebarLayoutStore()

const quickItems = computed(() => quickAccess.items)
const accountName = computed(() => auth.user?.userName ?? '')

const quickSettingsVisible = ref(false)
const removingQuickId = ref<number | null>(null)

// ---------------- 容器测量与尺寸恢复 ----------------
const shellRef = ref<HTMLElement | null>(null)
/** 内容区至少保留的宽度，避免侧栏把文件列表挤没。 */
const MIN_CONTENT_WIDTH = 240
/** 水平分隔条高度，与样式中的 height 保持一致。 */
const SPLITTER_HEIGHT = 9
/** 目录树至少保留的高度，避免被上部分挤没。 */
const TREE_MIN_HEIGHT = 120

/** 侧栏所在的页签内容容器（宽度/高度的夹取基准）。 */
function containerElement(): HTMLElement | null {
  return shellRef.value?.parentElement ?? null
}

function containerWidth(): number {
  return containerElement()?.clientWidth ?? 0
}

function containerHeight(): number {
  return containerElement()?.clientHeight ?? 0
}

/** 按容器宽度计算侧栏可用的最大宽度。 */
function contentMaxWidth(): number {
  const total = containerWidth()
  if (total <= 0) {
    return QUICK_ACCESS_MAX_WIDTH
  }

  return total - MIN_CONTENT_WIDTH
}

/** 按容器高度计算上部分可用的最大高度（为分隔条与目录树留出空间）。 */
function contentMaxHeight(): number {
  const total = containerHeight()
  if (total <= 0) {
    // 隐藏页签尺寸为 0，先不限制，等可见后再按真实高度夹取。
    return Number.MAX_SAFE_INTEGER
  }

  return total - SPLITTER_HEIGHT - TREE_MIN_HEIGHT
}

/** 恢复账号保存的宽高；账号尚未恢复时保持默认值，稍后由 watch 补读。 */
function restoreLayout(): void {
  if (!accountName.value) {
    return
  }

  layout.restore(accountName.value, contentMaxWidth(), contentMaxHeight())
}

/** 容器尺寸变化时重新夹取；隐藏页签（尺寸 0）跳过，避免压到最小值。 */
function clampToContainer(): void {
  if (containerWidth() > 0) {
    layout.setWidth(layout.width, contentMaxWidth())
  }

  if (containerHeight() > 0) {
    layout.setHeight(layout.height, contentMaxHeight())
  }
}

// ---------------- 宽度拖拽（账号级共享） ----------------
const resizing = ref(false)
let resizeStartX = 0
let resizeStartWidth = 0
let resizePointerId: number | null = null
let previousBodyUserSelect = ''
let previousBodyCursor = ''

function startResize(event: PointerEvent): void {
  if (event.button !== 0) {
    return
  }

  event.preventDefault()
  const handle = event.currentTarget as HTMLElement
  handle.setPointerCapture(event.pointerId)
  resizePointerId = event.pointerId
  resizeStartX = event.clientX
  resizeStartWidth = layout.width
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

  layout.setWidth(resizeStartWidth + (event.clientX - resizeStartX), contentMaxWidth())
}

/** 结束拖拽；pointerup / pointercancel / lostpointercapture 都会走到这里。 */
function finishResize(event: PointerEvent): void {
  if (!resizing.value || event.pointerId !== resizePointerId) {
    return
  }

  resizing.value = false
  resizePointerId = null
  releaseDragStyles()
  layout.persistWidth()
}

function releaseDragStyles(): void {
  document.body.style.userSelect = previousBodyUserSelect
  document.body.style.cursor = previousBodyCursor
  previousBodyUserSelect = ''
  previousBodyCursor = ''
}

/** 双击分隔条恢复默认宽度并保存。 */
function resetQuickWidth(): void {
  layout.setWidth(QUICK_ACCESS_DEFAULT_WIDTH, contentMaxWidth())
  layout.persistWidth()
}

/** 键盘可达：左右方向键每次调整 16px，并保存。 */
function onResizeKeydown(event: KeyboardEvent): void {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
    return
  }

  event.preventDefault()
  const delta = event.key === 'ArrowLeft' ? -16 : 16
  layout.setWidth(layout.width + delta, contentMaxWidth())
  layout.persistWidth()
}

// ---------------- 高度拖拽（账号级共享） ----------------
const resizingHeight = ref(false)
let heightStartY = 0
let heightStartValue = 0
let heightPointerId: number | null = null
let previousHeightBodyUserSelect = ''
let previousHeightBodyCursor = ''

function startHeightResize(event: PointerEvent): void {
  if (event.button !== 0) {
    return
  }

  event.preventDefault()
  const handle = event.currentTarget as HTMLElement
  handle.setPointerCapture(event.pointerId)
  heightPointerId = event.pointerId
  heightStartY = event.clientY
  heightStartValue = layout.height
  resizingHeight.value = true
  previousHeightBodyUserSelect = document.body.style.userSelect
  previousHeightBodyCursor = document.body.style.cursor
  document.body.style.userSelect = 'none'
  document.body.style.cursor = 'row-resize'
}

function onHeightResizeMove(event: PointerEvent): void {
  if (!resizingHeight.value || event.pointerId !== heightPointerId) {
    return
  }

  layout.setHeight(heightStartValue + (event.clientY - heightStartY), contentMaxHeight())
}

/** 结束拖拽；pointerup / pointercancel / lostpointercapture 都会走到这里。 */
function finishHeightResize(event: PointerEvent): void {
  if (!resizingHeight.value || event.pointerId !== heightPointerId) {
    return
  }

  resizingHeight.value = false
  heightPointerId = null
  releaseHeightDragStyles()
  layout.persistHeight()
}

function releaseHeightDragStyles(): void {
  document.body.style.userSelect = previousHeightBodyUserSelect
  document.body.style.cursor = previousHeightBodyCursor
  previousHeightBodyUserSelect = ''
  previousHeightBodyCursor = ''
}

/** 双击分隔条恢复默认高度并保存。 */
function resetQuickHeight(): void {
  layout.setHeight(QUICK_ACCESS_DEFAULT_HEIGHT, contentMaxHeight())
  layout.persistHeight()
}

/** 键盘可达：上下方向键每次调整 16px，并保存。 */
function onHeightResizeKeydown(event: KeyboardEvent): void {
  if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') {
    return
  }

  event.preventDefault()
  const delta = event.key === 'ArrowUp' ? -16 : 16
  layout.setHeight(layout.height + delta, contentMaxHeight())
  layout.persistHeight()
}

/** 窗口尺寸变化时重新夹取，避免目录树被挤成 0 高；不强制覆盖已保存值。 */
function onWindowResize(): void {
  clampToContainer()
}

// ---------------- 快速访问条目 ----------------
/** 请求所属文件页签跳转到该目录；隐藏页签不会收到点击。 */
function navigate(path: string): void {
  emit('navigate', path)
}

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

/** 从管理抽屉移除条目；共享 store 更新后所有页签的列表立即同步。 */
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

let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  restoreLayout()
  // 共享 store 已加载时直接复用，未加载或上次失败时重试。
  void quickAccess.load()
  void nextTick(() => clampToContainer())
  window.addEventListener('resize', onWindowResize)

  const container = containerElement()
  if (container && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => clampToContainer())
    resizeObserver.observe(container)
  }
})

// 会话恢复较晚时账号名可能稍后才可用，补读一次该账号的宽高。
watch(accountName, (name) => {
  if (name) {
    restoreLayout()
  }
})

// 列表加载完成后侧栏才显示，此时按真实容器再夹取一次。
watch(
  () => quickAccess.loaded,
  (loaded) => {
    if (loaded) {
      void nextTick(() => clampToContainer())
      return
    }

    // 列表被隐藏（接口失败或账号重置）时分隔条随 v-if 卸载，兜底结束拖拽。
    if (resizing.value) {
      resizing.value = false
      resizePointerId = null
      releaseDragStyles()
      layout.persistWidth()
    }

    if (resizingHeight.value) {
      resizingHeight.value = false
      heightPointerId = null
      releaseHeightDragStyles()
      layout.persistHeight()
    }
  },
)

onBeforeUnmount(() => {
  window.removeEventListener('resize', onWindowResize)
  resizeObserver?.disconnect()
  resizeObserver = null

  if (resizing.value) {
    resizing.value = false
    resizePointerId = null
    releaseDragStyles()
  }

  if (resizingHeight.value) {
    resizingHeight.value = false
    heightPointerId = null
    releaseHeightDragStyles()
  }
})
</script>

<template>
  <div ref="shellRef" class="sidebar-shell">
    <!-- 接口不可用（403/503）时 loaded=false，侧栏整体隐藏，把宽度让给文件列表 -->
    <template v-if="quickAccess.loaded">
      <aside class="sidebar" :style="{ width: `${layout.width}px` }">
        <div class="quick-access" :style="{ height: `${layout.height}px` }">
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
              :class="{ active: item.path === currentPath }"
              :title="item.path"
              @click="navigate(item.path)"
            >
              <el-icon class="quick-item-icon"><Folder /></el-icon>
              <span class="quick-item-name">{{ item.name }}</span>
            </button>
          </div>
        </div>

        <!-- 上下的高度分隔条：松手后按账号保存，双击恢复默认高度 -->
        <div
          class="splitter-h"
          :class="{ resizing: resizingHeight }"
          role="separator"
          aria-orientation="horizontal"
          aria-label="调整快速访问栏高度"
          :aria-valuenow="layout.height"
          :aria-valuemin="QUICK_ACCESS_MIN_HEIGHT"
          :aria-valuemax="contentMaxHeight()"
          tabindex="0"
          title="拖动调整高度，双击恢复默认"
          @pointerdown="startHeightResize"
          @pointermove="onHeightResizeMove"
          @pointerup="finishHeightResize"
          @pointercancel="finishHeightResize"
          @lostpointercapture="finishHeightResize"
          @dblclick="resetQuickHeight"
          @keydown="onHeightResizeKeydown"
        />

        <div class="tree-panel">
          <DirectoryTree
            :roots="roots"
            :home-path="homePath"
            :current-path="currentPath"
            @navigate="navigate"
          />
        </div>
      </aside>

      <!-- 拖拽分隔条：松手后按当前登录账号保存宽度，双击恢复默认值 -->
      <div
        class="resizer"
        :class="{ resizing }"
        role="separator"
        aria-orientation="vertical"
        aria-label="调整快速访问栏宽度"
        :aria-valuenow="layout.width"
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
    </template>

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
  </div>
</template>

<style scoped>
/* 侧栏与竖向分隔条一起占位；侧栏宽高由账号级共享状态控制 */
.sidebar-shell {
  display: flex;
  flex: none;
  align-self: stretch;
  min-height: 0;
}

.sidebar {
  display: flex;
  flex: none;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

/* 上部分高度由水平分隔条控制并按账号记忆；设置按钮独立于右上角的文件管理设置齿轮 */
.quick-access {
  display: flex;
  flex: none;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
}

/* 水平分隔条：整条 9px 为拖拽热区，中间 40px 横条只做视觉提示 */
.splitter-h {
  box-sizing: border-box;
  position: relative;
  flex: none;
  height: 9px;
  border-top: 1px solid #ebeef5;
  cursor: row-resize;
  touch-action: none;
  outline: none;
}

.splitter-h::before {
  content: '';
  position: absolute;
  top: 50%;
  left: 50%;
  width: 40px;
  height: 3px;
  border-radius: 2px;
  background: #dcdfe6;
  transform: translate(-50%, -50%);
  transition: background-color 0.15s;
}

.splitter-h:hover::before,
.splitter-h.resizing::before,
.splitter-h:focus-visible::before {
  background: #409eff;
}

.splitter-h:focus-visible {
  outline: 2px solid #409eff;
  outline-offset: -2px;
}

/* 下部分目录树占满剩余高度，内部自行滚动 */
.tree-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
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

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}
</style>
