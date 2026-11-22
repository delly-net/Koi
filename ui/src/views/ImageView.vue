<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { InputInstance } from 'element-plus'
import { ElMessage } from 'element-plus'
import {
  ArrowLeft,
  ArrowRight,
  Close,
  FolderOpened,
  Picture,
  Refresh,
  RefreshLeft,
  RefreshRight,
  ZoomIn,
  ZoomOut,
} from '@element-plus/icons-vue'
import {
  buildImageUrl,
  buildThumbnailUrl,
  getFileRoots,
  listImages,
  type FileImageEntry,
} from '@/api/files'
import { ApiError } from '@/api/http'
import FilePickerDialog from '@/components/files/FilePickerDialog.vue'
import { useImageBrowserStore, type ImageBrowserRequest } from '@/stores/imageBrowser'

const imageBrowser = useImageBrowserStore()

const currentPath = ref('')
const images = ref<FileImageEntry[]>([])
const truncated = ref(false)
const loading = ref(false)
const loadError = ref('')
const homePath = ref('')
const activePath = ref('')
const gridListRef = ref<HTMLElement | null>(null)

// ---------------- 目录选择 ----------------
const dirDialog = ref(false)
const dirInput = ref('')
const dirLoading = ref(false)
const dirInputRef = ref<InputInstance | null>(null)
const pickerVisible = ref(false)
const pickerInitial = ref('')

// ---------------- 全屏查看器 ----------------
const viewerOpen = ref(false)
const viewerIndex = ref(-1)
const scale = ref(1)
const rotation = ref(0)
const flipX = ref(false)
const flipY = ref(false)
const panX = ref(0)
const panY = ref(0)
const dragging = ref(false)
const viewerError = ref('')

const dragStartX = ref(0)
const dragStartY = ref(0)
const dragStartPanX = ref(0)
const dragStartPanY = ref(0)
let previousBodyOverflow = ''

const MIN_SCALE = 0.1
const MAX_SCALE = 8

const currentImage = computed<FileImageEntry | null>(
  () => images.value[viewerIndex.value] ?? null,
)

const viewerSrc = computed(() =>
  currentImage.value
    ? buildImageUrl(currentImage.value.path, currentImage.value.modifiedAt)
    : '',
)

const canPrev = computed(() => viewerIndex.value > 0)
const canNext = computed(() => viewerIndex.value >= 0 && viewerIndex.value < images.value.length - 1)
const zoomPercent = computed(() => Math.round(scale.value * 100))

/** 平移、缩放、旋转与双向翻转组合；翻转独立于缩放，避免叠加成倍翻转。 */
const imageTransform = computed(
  () =>
    `translate(${panX.value}px, ${panY.value}px) scale(${scale.value}) rotate(${rotation.value}deg) ` +
    `scaleX(${flipX.value ? -1 : 1}) scaleY(${flipY.value ? -1 : 1})`,
)

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    return error.detail ? `${error.message}：${error.detail}` : error.message
  }

  return error instanceof Error ? error.message : '加载图片失败'
}

// ---------------- 加载图片列表 ----------------

/**
 * 加载目录下的图片（后端递归扫描子目录）。
 * 失败时保留当前目录与列表，错误以常驻告警展示，便于重试。
 */
async function load(path?: string, selectPath = ''): Promise<boolean> {
  loading.value = true
  loadError.value = ''
  try {
    const data = await listImages(path)
    currentPath.value = data.path
    images.value = data.images
    truncated.value = data.truncated

    if (selectPath) {
      const index = images.value.findIndex((item) => item.path === selectPath)
      if (index >= 0) {
        activePath.value = selectPath
        await nextTick()
        gridListRef.value?.children[index]?.scrollIntoView({ block: 'center' })
        openViewer(index)
      }
    }

    return true
  } catch (error) {
    loadError.value = errorText(error)
    return false
  } finally {
    loading.value = false
  }
}

function reload(): void {
  void load(currentPath.value || undefined)
}

async function handleRequest(request: ImageBrowserRequest): Promise<void> {
  if (!request.directory) {
    return
  }

  await load(request.directory, request.selectPath)
}

function consumePending(): void {
  const pending = imageBrowser.consume()
  if (pending) {
    void handleRequest(pending)
    return
  }

  // 首次打开且没有外部请求时加载账号家目录，页面不会停在空白状态。
  void load()
}

watch(
  () => imageBrowser.request,
  (pending) => {
    if (!pending) {
      return
    }

    // 消费后立即清空，避免同一请求被重复处理。
    imageBrowser.consume()
    void handleRequest(pending)
  },
)

// ---------------- 按路径打开目录 ----------------

function joinPath(directory: string, name: string): string {
  return directory === '/' ? `/${name}` : `${directory.replace(/\/+$/, '')}/${name}`
}

/** 惰性读取家目录，用于展开 ~ 与文件浏览的初始位置；失败时保持为空。 */
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
 * 图片浏览页没有相对路径的基准目录，其余输入直接提示用户。
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

function openDirDialog(): void {
  dirInput.value = currentPath.value
  dirDialog.value = true
  void ensureHomePath()
  void nextTick(() => dirInputRef.value?.focus())
}

async function submitDir(): Promise<void> {
  if (dirLoading.value) {
    return
  }

  const target = await resolveOpenPath(dirInput.value)
  if (!target) {
    return
  }

  dirLoading.value = true
  try {
    if (await load(target)) {
      dirDialog.value = false
      dirInput.value = ''
    }
  } finally {
    dirLoading.value = false
  }
}

async function openPicker(): Promise<void> {
  pickerInitial.value = currentPath.value || (await ensureHomePath())
  pickerVisible.value = true
}

/** 浏览对话框确认后只回填路径，由用户再点【确定】加载。 */
function onPicked(path: string): void {
  dirInput.value = path
  pickerVisible.value = false
  void nextTick(() => dirInputRef.value?.focus())
}

// ---------------- 全屏查看器 ----------------

function resetTransform(): void {
  scale.value = 1
  rotation.value = 0
  flipX.value = false
  flipY.value = false
  panX.value = 0
  panY.value = 0
  viewerError.value = ''
}

function openViewer(index: number): void {
  if (index < 0 || index >= images.value.length) {
    return
  }

  viewerIndex.value = index
  activePath.value = images.value[index].path
  resetTransform()
  viewerOpen.value = true
}

function closeViewer(): void {
  viewerOpen.value = false
  viewerIndex.value = -1
}

function stepViewer(delta: number): void {
  const next = viewerIndex.value + delta
  if (next < 0 || next >= images.value.length) {
    return
  }

  viewerIndex.value = next
  activePath.value = images.value[next].path
  resetTransform()
}

function zoomBy(factor: number): void {
  const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale.value * factor))
  scale.value = Number(next.toFixed(3))
}

function rotateBy(degrees: number): void {
  rotation.value = (rotation.value + degrees) % 360
}

function onWheel(event: WheelEvent): void {
  zoomBy(event.deltaY < 0 ? 1.1 : 1 / 1.1)
}

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) {
    return
  }

  dragging.value = true
  dragStartX.value = event.clientX
  dragStartY.value = event.clientY
  dragStartPanX.value = panX.value
  dragStartPanY.value = panY.value
  ;(event.currentTarget as HTMLElement | null)?.setPointerCapture(event.pointerId)
}

function onPointerMove(event: PointerEvent): void {
  if (!dragging.value) {
    return
  }

  panX.value = dragStartPanX.value + event.clientX - dragStartX.value
  panY.value = dragStartPanY.value + event.clientY - dragStartY.value
}

function onPointerUp(event: PointerEvent): void {
  if (!dragging.value) {
    return
  }

  dragging.value = false
  ;(event.currentTarget as HTMLElement | null)?.releasePointerCapture(event.pointerId)
}

function onViewerImageError(): void {
  viewerError.value = '图片加载失败，或浏览器不支持该图片格式。'
}

/** 查看器打开时的键盘快捷键：Esc 关闭、←/→ 切换、+/- 缩放、0 重置、R 旋转。 */
function onViewerKeydown(event: KeyboardEvent): void {
  if (!viewerOpen.value) {
    return
  }

  switch (event.key) {
    case 'Escape':
      closeViewer()
      break
    case 'ArrowLeft':
      stepViewer(-1)
      event.preventDefault()
      break
    case 'ArrowRight':
      stepViewer(1)
      event.preventDefault()
      break
    case '+':
    case '=':
      zoomBy(1.1)
      event.preventDefault()
      break
    case '-':
    case '_':
      zoomBy(1 / 1.1)
      event.preventDefault()
      break
    case '0':
      resetTransform()
      event.preventDefault()
      break
    case 'r':
    case 'R':
      rotateBy(90)
      event.preventDefault()
      break
    default:
      break
  }
}

watch(viewerOpen, (open) => {
  if (typeof document === 'undefined') {
    return
  }

  if (open) {
    previousBodyOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
  } else {
    document.body.style.overflow = previousBodyOverflow
  }
})

watch(
  () => images.value.length,
  (length) => {
    if (viewerIndex.value >= length) {
      closeViewer()
    }
  },
)

onMounted(() => {
  window.addEventListener('keydown', onViewerKeydown)
  consumePending()
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onViewerKeydown)
  if (typeof document !== 'undefined') {
    document.body.style.overflow = previousBodyOverflow
  }
})
</script>

<template>
  <div class="image-view">
    <div class="toolbar">
      <span class="title">图片浏览</span>
      <span class="path" :title="currentPath">{{ currentPath || '—' }}</span>
      <span class="count">{{ images.length }} 张</span>

      <div class="actions">
        <el-button :icon="FolderOpened" @click="openDirDialog">打开目录</el-button>
        <el-button :icon="Refresh" :loading="loading" @click="reload">刷新</el-button>
      </div>
    </div>

    <el-alert
      v-if="loadError"
      type="error"
      :closable="false"
      show-icon
      :title="loadError"
      class="page-alert"
    >
      <template #default>
        <el-button size="small" :loading="loading" @click="reload">重试</el-button>
      </template>
    </el-alert>

    <el-alert
      v-if="truncated"
      type="warning"
      :closable="false"
      show-icon
      title="图片数量或目录深度达到上限，仅显示部分结果；可缩小目录范围后重试。"
      class="page-alert"
    />

    <div v-loading="loading" class="grid-wrap">
      <div v-if="images.length > 0" ref="gridListRef" class="grid">
        <button
          v-for="(image, index) in images"
          :key="image.path"
          type="button"
          class="tile"
          :class="{ selected: activePath === image.path }"
          :aria-label="`查看图片 ${image.relativePath}`"
          @click="openViewer(index)"
        >
          <el-image
            class="thumb"
            :src="buildThumbnailUrl(image.path, image.modifiedAt)"
            fit="contain"
            lazy
          >
            <template #placeholder>
              <div class="thumb-placeholder">
                <el-icon><Picture /></el-icon>
              </div>
            </template>
            <template #error>
              <div class="thumb-placeholder">
                <el-icon><Picture /></el-icon>
                <span class="thumb-alt">无法预览</span>
              </div>
            </template>
          </el-image>
          <span class="tile-name" :title="image.relativePath">{{ image.relativePath }}</span>
        </button>
      </div>

      <el-empty
        v-else-if="!loading && !loadError"
        description="该目录（含子目录）下没有受支持的图片"
        class="empty"
      />
    </div>

    <el-dialog
      v-model="dirDialog"
      title="打开目录"
      width="560px"
      :close-on-click-modal="false"
      @closed="dirInput = ''"
    >
      <el-form label-position="top" @submit.prevent>
        <el-form-item label="目录路径">
          <el-input
            ref="dirInputRef"
            v-model="dirInput"
            clearable
            placeholder="如 /home/user/Pictures 或 ~/Pictures"
            @keyup.enter="submitDir"
          >
            <template #append>
              <el-button :icon="FolderOpened" @click="openPicker">浏览...</el-button>
            </template>
          </el-input>
        </el-form-item>
      </el-form>
      <div class="open-hint">
        支持 / 开头的绝对路径，以及 ~（家目录）与 ~/子目录；会递归加载所选目录（含子目录）中的图片。可访问范围仍由登录用户的系统权限与文件管理允许根决定。
      </div>
      <template #footer>
        <el-button @click="dirDialog = false">取消</el-button>
        <el-button type="primary" :loading="dirLoading" @click="submitDir">确定</el-button>
      </template>
    </el-dialog>

    <FilePickerDialog
      v-model="pickerVisible"
      mode="directory"
      :initial-path="pickerInitial"
      :home-path="homePath"
      @select="onPicked"
    />

    <Teleport to="body">
      <div
        v-if="viewerOpen && currentImage"
        class="viewer"
        role="dialog"
        aria-modal="true"
        :aria-label="`查看图片 ${currentImage.relativePath}`"
      >
        <header class="viewer-bar">
          <div class="viewer-meta">
            <span class="viewer-name" :title="currentImage.relativePath">
              {{ currentImage.relativePath }}
            </span>
            <span class="viewer-count">{{ viewerIndex + 1 }} / {{ images.length }}</span>
          </div>

          <div class="viewer-actions">
            <el-button text class="viewer-button" :icon="ZoomOut" @click="zoomBy(1 / 1.1)">
              缩小
            </el-button>
            <span class="viewer-zoom">{{ zoomPercent }}%</span>
            <el-button text class="viewer-button" :icon="ZoomIn" @click="zoomBy(1.1)">
              放大
            </el-button>
            <el-button text class="viewer-button" :icon="RefreshLeft" @click="rotateBy(-90)">
              左转
            </el-button>
            <el-button text class="viewer-button" :icon="RefreshRight" @click="rotateBy(90)">
              右转
            </el-button>
            <el-button text class="viewer-button" @click="flipX = !flipX">水平翻转</el-button>
            <el-button text class="viewer-button" @click="flipY = !flipY">垂直翻转</el-button>
            <el-button text class="viewer-button" @click="resetTransform">重置</el-button>
            <el-button
              text
              class="viewer-button viewer-close"
              :icon="Close"
              aria-label="关闭查看器"
              @click="closeViewer"
            />
          </div>
        </header>

        <div
          class="viewer-stage"
          :class="{ dragging }"
          @wheel.prevent="onWheel"
          @pointerdown="onPointerDown"
          @pointermove="onPointerMove"
          @pointerup="onPointerUp"
          @pointercancel="onPointerUp"
        >
          <el-alert
            v-if="viewerError"
            type="error"
            :closable="false"
            show-icon
            :title="viewerError"
            class="viewer-error"
          />
          <img
            v-else
            :key="currentImage.path"
            :src="viewerSrc"
            :alt="currentImage.name"
            class="viewer-image"
            :style="{ transform: imageTransform }"
            draggable="false"
            @error="onViewerImageError"
          />
        </div>

        <button
          type="button"
          class="viewer-arrow viewer-arrow-left"
          :disabled="!canPrev"
          aria-label="上一张"
          @click="stepViewer(-1)"
        >
          <el-icon :size="36"><ArrowLeft /></el-icon>
        </button>
        <button
          type="button"
          class="viewer-arrow viewer-arrow-right"
          :disabled="!canNext"
          aria-label="下一张"
          @click="stepViewer(1)"
        >
          <el-icon :size="36"><ArrowRight /></el-icon>
        </button>

        <footer class="viewer-hint">
          滚轮缩放 · 拖动平移 · ←/→ 切换上一张/下一张 · Esc 关闭
        </footer>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.image-view {
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
  max-width: 46vw;
  color: #909399;
  font-family: ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.count {
  flex: none;
  color: #909399;
  font-size: 12px;
}

.actions {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.page-alert {
  flex: none;
  margin-bottom: 8px;
}

.grid-wrap {
  flex: 1;
  min-height: 0;
  padding: 12px;
  overflow: auto;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, 128px);
  gap: 16px;
  justify-content: start;
}

.tile {
  display: flex;
  flex-direction: column;
  gap: 6px;
  width: 128px;
  padding: 0;
  background: transparent;
  border: none;
  cursor: pointer;
  text-align: left;
}

.thumb {
  box-sizing: border-box;
  width: 128px;
  height: 128px;
  overflow: hidden;
  background: #f2f3f5;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
  transition:
    border-color 0.2s ease,
    box-shadow 0.2s ease;
}

.tile:hover .thumb,
.tile:focus-visible .thumb {
  border-color: var(--el-color-primary-light-5);
  box-shadow: 0 0 0 2px var(--el-color-primary-light-8);
}

.tile.selected .thumb {
  border-color: var(--el-color-primary);
  box-shadow: 0 0 0 2px var(--el-color-primary-light-7);
}

.tile:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
  border-radius: 6px;
}

.thumb-placeholder {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 128px;
  height: 128px;
  color: #c0c4cc;
  font-size: 28px;
}

.thumb-alt {
  font-size: 12px;
}

.tile-name {
  width: 128px;
  overflow: hidden;
  color: #606266;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.empty {
  margin-top: 48px;
}

.open-hint {
  color: #8a919f;
  font-size: 12px;
  line-height: 1.6;
}

/* ---------------- 全屏查看器 ---------------- */

.viewer {
  position: fixed;
  inset: 0;
  z-index: 4000;
  display: flex;
  flex-direction: column;
  background: rgba(0, 0, 0, 0.92);
  color: #ffffff;
  user-select: none;
}

.viewer-bar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 16px;
  min-height: 52px;
  padding: 8px 72px 8px 20px;
  background: rgba(0, 0, 0, 0.35);
}

.viewer-meta {
  display: flex;
  flex: 1;
  align-items: baseline;
  gap: 12px;
  min-width: 0;
}

.viewer-name {
  overflow: hidden;
  font-size: 14px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.viewer-count {
  flex: none;
  color: rgba(255, 255, 255, 0.65);
  font-size: 12px;
}

.viewer-actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 4px;
}

.viewer-button {
  --el-button-text-color: rgba(255, 255, 255, 0.9);
  --el-button-hover-text-color: #ffffff;
  --el-button-hover-bg-color: rgba(255, 255, 255, 0.12);
  --el-button-hover-link-text-color: #ffffff;
  margin-left: 0 !important;
  padding: 4px 8px;
}

.viewer-zoom {
  min-width: 52px;
  color: rgba(255, 255, 255, 0.75);
  font-size: 12px;
  text-align: center;
}

.viewer-stage {
  position: relative;
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  min-height: 0;
  overflow: hidden;
  cursor: grab;
  touch-action: none;
}

.viewer-stage.dragging {
  cursor: grabbing;
}

.viewer-image {
  max-width: 82vw;
  max-height: 74vh;
  object-fit: contain;
  transform-origin: center center;
  will-change: transform;
  pointer-events: none;
}

.viewer-error {
  width: min(560px, 80vw);
}

.viewer-arrow {
  position: absolute;
  top: 50%;
  z-index: 2;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 56px;
  height: 56px;
  padding: 0;
  color: rgba(255, 255, 255, 0.92);
  background: rgba(0, 0, 0, 0.38);
  border: 1px solid rgba(255, 255, 255, 0.18);
  border-radius: 50%;
  cursor: pointer;
  transform: translateY(-50%);
  transition:
    background-color 0.2s ease,
    opacity 0.2s ease;
}

.viewer-arrow:hover:not(:disabled) {
  background: rgba(0, 0, 0, 0.62);
}

.viewer-arrow:disabled {
  cursor: default;
  opacity: 0.18;
}

.viewer-arrow-left {
  left: 20px;
}

.viewer-arrow-right {
  right: 20px;
}

.viewer-hint {
  flex: none;
  padding: 8px 16px 12px;
  color: rgba(255, 255, 255, 0.55);
  font-size: 12px;
  text-align: center;
}

@media (max-width: 900px) {
  .viewer-bar {
    flex-wrap: wrap;
    padding: 8px 16px;
  }

  .viewer-actions {
    flex-wrap: wrap;
  }

  .viewer-arrow {
    width: 44px;
    height: 44px;
  }

  .viewer-arrow-left {
    left: 8px;
  }

  .viewer-arrow-right {
    right: 8px;
  }
}
</style>
