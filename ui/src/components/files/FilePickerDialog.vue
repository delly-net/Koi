<script setup lang="ts">
import { computed, ref } from 'vue'
import { Document, Folder } from '@element-plus/icons-vue'
import type { FileEntry } from '@/api/files'
import { listFiles } from '@/api/files'
import { ApiError } from '@/api/http'

interface Crumb {
  label: string
  path: string
}

const props = withDefaults(
  defineProps<{
    /** 对话框显隐，配合 v-model 使用。 */
    modelValue: boolean
    /** 打开时定位的目录；为空表示登录用户的家目录。 */
    initialPath?: string
    /** 登录用户的家目录，用于「家目录」快捷按钮。 */
    homePath?: string
    /**
     * 选择模式：`file`（默认）单击文件选中、双击确认；
     * `directory` 单击目录进入，底部「选择当前目录」回传当前目录。
     */
    mode?: 'file' | 'directory'
  }>(),
  { mode: 'file' },
)

const emit = defineEmits<{
  (e: 'update:modelValue', value: boolean): void
  /** 确认选择后回传文件或目录的绝对路径。 */
  (e: 'select', path: string): void
}>()

const currentPath = ref('')
const parentPath = ref<string | null>(null)
const entries = ref<FileEntry[]>([])
const truncated = ref(false)
const loading = ref(false)
const loadError = ref('')
const selected = ref<FileEntry | null>(null)

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

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    return error.detail ? `${error.message}：${error.detail}` : error.message
  }

  return error instanceof Error ? error.message : '读取目录失败'
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

/** 只负责按路径列目录；失败时保留当前目录与前一次列表，便于重试。 */
async function load(path?: string): Promise<void> {
  loading.value = true
  loadError.value = ''
  try {
    const data = await listFiles(path)
    currentPath.value = data.path
    parentPath.value = data.parent
    entries.value = data.entries
    truncated.value = data.truncated
    selected.value = null
  } catch (error) {
    loadError.value = errorText(error)
  } finally {
    loading.value = false
  }
}

function reload(): void {
  void load(currentPath.value || undefined)
}

/** 打开时按初始目录（或家目录）加载，并清空上一次的选择。 */
function handleOpen(): void {
  selected.value = null
  entries.value = []
  void load(props.initialPath || undefined)
}

function goHome(): void {
  if (props.homePath) {
    void load(props.homePath)
  }
}

/** 单击目录进入；文件模式下单击文件仅选中，双击文件等同确认。 */
function onRowClick(row: FileEntry): void {
  if (row.isDirectory) {
    void load(row.path)
    return
  }

  if (props.mode === 'file') {
    selected.value = row
  }
}

function onRowDblClick(row: FileEntry): void {
  if (!row.isDirectory && props.mode === 'file') {
    selected.value = row
    confirmSelect()
  }
}

function rowClassName({ row }: { row: FileEntry }): string {
  return selected.value?.path === row.path ? 'is-selected' : ''
}

function confirmSelect(): void {
  if (loading.value) {
    return
  }

  // 目录模式回传当前目录；文件模式必须已选中文件。
  const path = props.mode === 'directory' ? currentPath.value : selected.value?.path
  if (!path) {
    return
  }

  emit('select', path)
  emit('update:modelValue', false)
}

function close(): void {
  emit('update:modelValue', false)
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="mode === 'directory' ? '选择目录' : '选择文件'"
    width="720px"
    top="8vh"
    :close-on-click-modal="false"
    @update:model-value="(value: boolean) => emit('update:modelValue', value)"
    @open="handleOpen"
  >
    <div class="path-bar">
      <el-button text :disabled="!parentPath || loading" @click="parentPath && load(parentPath)">
        上一级
      </el-button>
      <el-button text :disabled="!homePath || loading" @click="goHome">家目录</el-button>
      <el-breadcrumb separator="/" class="crumbs">
        <el-breadcrumb-item v-for="crumb in breadcrumbs" :key="crumb.path">
          <a class="crumb" @click.prevent="load(crumb.path)">{{ crumb.label }}</a>
        </el-breadcrumb-item>
      </el-breadcrumb>
    </div>

    <el-alert
      v-if="loadError"
      type="error"
      :closable="false"
      show-icon
      :title="loadError"
      class="load-error"
    >
      <template #default>
        <el-button size="small" :loading="loading" @click="reload">重试</el-button>
      </template>
    </el-alert>

    <el-table
      v-loading="loading"
      :data="entries"
      height="380"
      row-key="path"
      :row-class-name="rowClassName"
      @row-click="onRowClick"
      @row-dblclick="onRowDblClick"
    >
      <el-table-column prop="name" label="名称" min-width="360">
        <template #default="{ row }">
          <span class="entry">
            <el-icon class="entry-icon" :class="{ folder: row.isDirectory }">
              <Folder v-if="row.isDirectory" />
              <Document v-else />
            </el-icon>
            <span class="entry-name">{{ row.name }}</span>
            <el-tag v-if="row.isSymlink" size="small" type="info" effect="plain">链接</el-tag>
          </span>
        </template>
      </el-table-column>
      <el-table-column prop="size" label="大小" width="100">
        <template #default="{ row }">{{ row.isDirectory ? '—' : formatSize(row.size) }}</template>
      </el-table-column>
      <el-table-column label="修改时间" width="180">
        <template #default="{ row }">{{ formatTime(row.modifiedAt) }}</template>
      </el-table-column>
    </el-table>

    <div class="status-bar">
      <span>{{ currentPath || '—' }}</span>
      <span>共 {{ entries.length }} 项</span>
      <span v-if="truncated" class="warning">目录条目过多，仅显示前 {{ entries.length }} 项</span>
    </div>

    <template #footer>
      <el-button @click="close">取消</el-button>
      <el-button
        type="primary"
        :disabled="mode === 'directory' ? !currentPath || loading : !selected || loading"
        @click="confirmSelect"
      >
        {{ mode === 'directory' ? '选择当前目录' : '选择' }}
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.path-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.crumbs {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
}

.crumb {
  color: #409eff;
  cursor: pointer;
}

.load-error {
  margin-bottom: 8px;
}

.entry {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  cursor: pointer;
}

.entry-icon {
  flex: none;
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

:deep(.is-selected td.el-table__cell) {
  background: var(--el-color-primary-light-9);
}

.status-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 6px;
  color: #909399;
  font-size: 12px;
}

.status-bar span:first-child {
  overflow: hidden;
  max-width: 60%;
  font-family: ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.warning {
  color: #e6a23c;
}
</style>
