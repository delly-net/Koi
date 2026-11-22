<script lang="ts">
/** 行操作菜单命令，与模板中 el-dropdown-item 的 command 一一对应。 */
export type FileRowAction =
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
</script>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { TableV2FixedDir } from 'element-plus'
import type { Column, ColumnSortParams } from 'element-plus'
import { Document, Folder, MoreFilled } from '@element-plus/icons-vue'
import type { FileEntry } from '@/api/files'
import { isTextFile } from '@/config/appConfig'
import { isImageFile } from '@/config/imageTypes'
import { formatFileSize, formatFileTime } from '@/utils/fileFormat'

/**
 * 文件列表的虚拟表格：只渲染可视区域行，供数千至数万条目的目录使用。
 * 勾选（含全选/半选）与三态排序在组件内部维护，业务动作通过事件交给父组件。
 */
const props = defineProps<{
  entries: FileEntry[]
}>()

const emit = defineEmits<{
  /** 点击名称：目录进入，文本文件进入在线编辑。 */
  (e: 'open', row: FileEntry): void
  /** 行操作菜单命令。 */
  (e: 'action', command: FileRowAction, row: FileEntry): void
  /** 当前选中的条目（始终取自已加载数据）。 */
  (e: 'selection-change', rows: FileEntry[]): void
}>()

type SortKey = 'name' | 'size' | 'modifiedAt'

const sortKey = ref<SortKey | ''>('')
const sortOrder = ref<'asc' | 'desc'>('asc')
const sortBy = computed(() => ({ key: sortKey.value, order: sortOrder.value }))

/** 无排序时直接复用服务端顺序（目录优先、名称升序），避免每批增量都拷贝数组。 */
const displayEntries = computed<FileEntry[]>(() => {
  const key = sortKey.value
  if (!key) {
    return props.entries
  }

  const direction = sortOrder.value === 'asc' ? 1 : -1
  const sorted = props.entries.slice()
  sorted.sort((left, right) => {
    if (key === 'name') {
      return left.name.localeCompare(right.name, 'zh-CN') * direction
    }

    if (key === 'size') {
      return (left.size - right.size) * direction
    }

    return (left.modifiedAt - right.modifiedAt) * direction
  })
  return sorted
})

/** 表头点击三态循环：升序 → 降序 → 恢复服务端默认顺序。 */
function onColumnSort({ key }: ColumnSortParams<FileEntry>): void {
  if (key !== 'name' && key !== 'size' && key !== 'modifiedAt') {
    return
  }

  const nextKey = key as SortKey
  if (sortKey.value !== nextKey) {
    sortKey.value = nextKey
    sortOrder.value = 'asc'
    return
  }

  if (sortOrder.value === 'asc') {
    sortOrder.value = 'desc'
    return
  }

  sortKey.value = ''
  sortOrder.value = 'asc'
}

const columns: Column<FileEntry>[] = [
  { key: 'selection', width: 44, fixed: TableV2FixedDir.LEFT, align: 'center' },
  { key: 'name', title: '名称', width: 260, minWidth: 260, flexGrow: 1, sortable: true },
  { key: 'size', title: '大小', width: 110, sortable: true },
  { key: 'modifiedAt', title: '修改时间', width: 190, sortable: true },
  { key: 'mode', title: '权限', width: 120 },
  { key: 'actions', title: '操作', width: 96, fixed: TableV2FixedDir.RIGHT, align: 'center' },
]

/** 勾选状态按路径维护：虚拟行回收/重建不会丢失选择。 */
const selectedPaths = ref(new Set<string>())

const allSelected = computed(
  () => props.entries.length > 0 && selectedPaths.value.size === props.entries.length,
)
const someSelected = computed(() => selectedPaths.value.size > 0 && !allSelected.value)

function isSelected(path: string): boolean {
  return selectedPaths.value.has(path)
}

function emitSelection(): void {
  emit(
    'selection-change',
    props.entries.filter((entry) => selectedPaths.value.has(entry.path)),
  )
}

function toggleRow(row: FileEntry): void {
  if (selectedPaths.value.has(row.path)) {
    selectedPaths.value.delete(row.path)
  } else {
    selectedPaths.value.add(row.path)
  }
  emitSelection()
}

/** 全选只作用于已加载条目；SSE 仍在推送时后续批次不会被自动选中。 */
function toggleAll(): void {
  if (allSelected.value) {
    selectedPaths.value.clear()
  } else {
    for (const entry of props.entries) {
      selectedPaths.value.add(entry.path)
    }
  }
  emitSelection()
}

function clearSelection(): void {
  selectedPaths.value.clear()
  emit('selection-change', [])
}

defineExpose({ clearSelection })

function openEntry(row: FileEntry): void {
  emit('open', row)
}

function onRowAction(command: FileRowAction, row: FileEntry): void {
  emit('action', command, row)
}

/** 仅 `.sh` 脚本（不区分大小写）提供「执行」，与父组件的实际执行入口保持一致。 */
function isShellScript(name: string): boolean {
  return name.toLowerCase().endsWith('.sh')
}
</script>

<template>
  <div class="file-table">
    <el-auto-resizer>
      <template #default="{ height, width }">
        <el-table-v2
          :columns="columns"
          :data="displayEntries"
          :width="width"
          :height="height"
          :row-height="40"
          :header-height="40"
          :sort-by="sortBy"
          :scrollbar-always-on="true"
          row-key="path"
          @column-sort="onColumnSort"
        >
          <template #header-cell="{ column }">
            <el-checkbox
              v-if="column.key === 'selection'"
              :model-value="allSelected"
              :indeterminate="someSelected"
              aria-label="全选"
              @change="toggleAll"
            />
            <span v-else class="header-title">{{ column.title }}</span>
          </template>

          <template #cell="{ column, rowData }">
            <div v-if="column.key === 'selection'" class="selection-cell">
              <el-checkbox
                :model-value="isSelected(rowData.path)"
                :aria-label="`选择 ${rowData.name}`"
                @change="toggleRow(rowData)"
              />
            </div>

            <span
              v-else-if="column.key === 'name'"
              class="entry"
              :class="{ openable: rowData.isDirectory || isTextFile(rowData.name) }"
              @click.stop="openEntry(rowData)"
            >
              <el-icon class="entry-icon" :class="{ folder: rowData.isDirectory }">
                <Folder v-if="rowData.isDirectory" />
                <Document v-else />
              </el-icon>
              <span class="entry-name">{{ rowData.name }}</span>
              <el-tag v-if="rowData.isSymlink" size="small" type="info" effect="plain">链接</el-tag>
            </span>

            <template v-else-if="column.key === 'size'">
              {{ rowData.isDirectory ? '—' : formatFileSize(rowData.size) }}
            </template>

            <template v-else-if="column.key === 'modifiedAt'">
              {{ formatFileTime(rowData.modifiedAt) }}
            </template>

            <template v-else-if="column.key === 'mode'">{{ rowData.mode }}</template>

            <el-dropdown
              v-else-if="column.key === 'actions'"
              trigger="click"
              @command="(command: FileRowAction) => onRowAction(command, rowData)"
            >
              <el-button size="small" :icon="MoreFilled">操作</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item
                    command="open"
                    :disabled="!rowData.isDirectory && !isTextFile(rowData.name)"
                  >
                    打开
                  </el-dropdown-item>
                  <el-dropdown-item command="newtab" :disabled="!rowData.isDirectory">
                    在新页签打开
                  </el-dropdown-item>
                  <el-dropdown-item command="terminal" :disabled="!rowData.isDirectory">
                    打开终端
                  </el-dropdown-item>
                  <el-dropdown-item command="quick" :disabled="!rowData.isDirectory">
                    添加到快速访问
                  </el-dropdown-item>
                  <el-dropdown-item
                    command="edit"
                    :disabled="rowData.isDirectory || !isTextFile(rowData.name)"
                  >
                    编辑文本
                  </el-dropdown-item>
                  <el-dropdown-item
                    command="images"
                    :disabled="!rowData.isDirectory && !isImageFile(rowData.name)"
                  >
                    浏览图片
                  </el-dropdown-item>
                  <el-dropdown-item
                    v-if="!rowData.isDirectory && isShellScript(rowData.name)"
                    command="run"
                  >
                    执行
                  </el-dropdown-item>
                  <el-dropdown-item command="download" :disabled="rowData.isDirectory">
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
          </template>
        </el-table-v2>
      </template>
    </el-auto-resizer>
  </div>
</template>

<style scoped>
.file-table {
  width: 100%;
  height: 100%;
}

.header-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.selection-cell {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
}

.entry {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
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

/* 下拉菜单传送到 body，scoped 样式无法命中，这里用全局选择器标记危险项 */
:global(.el-dropdown-menu__item.danger-item) {
  color: #f56c6c;
}
</style>
