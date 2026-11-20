<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { ArrowRight, Folder, Refresh } from '@element-plus/icons-vue'
import { listFiles } from '@/api/files'

/** 树节点：children 为 null 表示尚未加载，加载后即使为空也保留数组。 */
interface TreeNode {
  path: string
  name: string
  children: TreeNode[] | null
  loaded: boolean
  loading: boolean
  error: string
}

/** 扁平化后的一行；缩进由 depth 决定。 */
interface VisibleRow {
  node: TreeNode
  depth: number
  expanded: boolean
  /** 未加载时先显示箭头，加载后按真实子目录数量决定。 */
  hasChildren: boolean
}

const props = defineProps<{
  /** 允许访问的根目录；默认由后端返回 ["/"]。 */
  roots: string[]
  /** 登录账号家目录，根列表为空时作为兜底根。 */
  homePath: string
  /** 所属文件页签的当前目录，用于高亮与自动展开。 */
  currentPath: string
}>()

const emit = defineEmits<{
  /** 点击节点，请求所属文件页签跳转到该目录。 */
  (e: 'navigate', path: string): void
}>()

const rootNodes = ref<TreeNode[]>([])
const expandedPaths = ref<Set<string>>(new Set())
const rowRefs = new Map<string, HTMLElement>()
/** 同一目录并发展开时复用同一个请求，避免重复列举。 */
const pendingLoads = new Map<string, Promise<boolean>>()
/** 递增序号用于丢弃过期的自动定位结果。 */
let revealSeq = 0

function createNode(path: string, name: string): TreeNode {
  return { path, name, children: null, loaded: false, loading: false, error: '' }
}

function baseName(path: string): string {
  if (!path || path === '/') {
    return '/'
  }

  const parts = path.split('/').filter(Boolean)
  return parts[parts.length - 1] ?? '/'
}

/** path 是否等于 outer 或位于 outer 之下。 */
function containsPath(outer: string, inner: string): boolean {
  if (outer === '/') {
    return inner.startsWith('/')
  }

  const normalized = outer.replace(/\/+$/, '')
  return inner === normalized || inner.startsWith(`${normalized}/`)
}

/**
 * 根目录去重：AllowedRoots 可能同时包含 / 与 /home 这类嵌套根，
 * 只保留外层根，避免同一目录在树中重复出现。
 */
function normalizeRoots(): string[] {
  const candidates = [...new Set(props.roots.filter((root) => root.startsWith('/')))]
  candidates.sort((left, right) => left.length - right.length)

  const result: string[] = []
  for (const root of candidates) {
    if (!result.some((outer) => containsPath(outer, root))) {
      result.push(root)
    }
  }

  if (result.length === 0 && props.homePath.startsWith('/')) {
    result.push(props.homePath)
  }

  return result
}

function buildTree(): void {
  rootNodes.value = normalizeRoots().map((path) => createNode(path, baseName(path)))
  expandedPaths.value = new Set()
  pendingLoads.clear()
  rowRefs.clear()
}

/** 加载某个目录的直属子目录；成功后缓存到节点上。 */
function loadChildren(node: TreeNode): Promise<boolean> {
  if (node.loaded) {
    return Promise.resolve(true)
  }

  const pending = pendingLoads.get(node.path)
  if (pending) {
    return pending
  }

  node.loading = true
  node.error = ''
  const task = (async (): Promise<boolean> => {
    try {
      const data = await listFiles(node.path)
      node.children = data.entries
        .filter((entry) => entry.isDirectory)
        .map((entry) => createNode(entry.path, entry.name))
      node.loaded = true
      return true
    } catch (error) {
      // 单个目录不可读不应导致整棵树不可用；错误保留在节点上供用户重试。
      node.error = error instanceof Error ? error.message : '无法加载目录'
      return false
    } finally {
      node.loading = false
    }
  })()

  pendingLoads.set(node.path, task)
  void task.finally(() => {
    if (pendingLoads.get(node.path) === task) {
      pendingLoads.delete(node.path)
    }
  })

  return task
}

/** 合并展开集合，避免自动定位覆盖用户手动展开的其它目录。 */
function mergeExpanded(paths: Set<string>): void {
  const merged = new Set(expandedPaths.value)
  paths.forEach((path) => merged.add(path))
  expandedPaths.value = merged
}

async function expandNode(node: TreeNode): Promise<void> {
  mergeExpanded(new Set([node.path]))
  if (!node.loaded) {
    await loadChildren(node)
  }
}

function toggleNode(row: VisibleRow): void {
  if (row.expanded) {
    const next = new Set(expandedPaths.value)
    next.delete(row.node.path)
    expandedPaths.value = next
    return
  }

  void expandNode(row.node)
}

function splitSegments(rootPath: string, target: string): string[] {
  if (target === rootPath) {
    return []
  }

  const rest = rootPath === '/' ? target.slice(1) : target.slice(rootPath.length + 1)
  return rest.split('/').filter(Boolean)
}

/**
 * 自动展开到目标目录并高亮。
 * 只展开祖先目录（目标自身的展开状态保持不变），逐级加载以保证节点存在。
 */
async function revealPath(target: string): Promise<void> {
  if (!target) {
    return
  }

  const token = ++revealSeq
  const root = rootNodes.value.find((node) => containsPath(node.path, target))
  if (!root) {
    return
  }

  const expanded = new Set<string>([root.path])
  let current = root
  const rootLoaded = await loadChildren(root)
  if (token !== revealSeq) {
    return
  }
  if (!rootLoaded) {
    mergeExpanded(expanded)
    return
  }

  const segments = splitSegments(root.path, target)
  for (let index = 0; index < segments.length; index += 1) {
    const child = current.children?.find((node) => node.name === segments[index])
    if (!child) {
      break
    }

    current = child
    // 仅展开还需要继续下钻的祖先目录；最后一段是目标本身，保持其原有展开状态。
    if (index < segments.length - 1) {
      expanded.add(current.path)
      const loaded = await loadChildren(current)
      if (token !== revealSeq) {
        return
      }
      if (!loaded) {
        break
      }
    }
  }

  mergeExpanded(expanded)
  await nextTick()
  if (token !== revealSeq) {
    return
  }

  rowRefs.get(target)?.scrollIntoView({ block: 'nearest' })
}

function setRowRef(path: string, element: unknown): void {
  if (element instanceof HTMLElement) {
    rowRefs.set(path, element)
  } else {
    rowRefs.delete(path)
  }
}

/** 点击行：先请求右侧跳转，再按需要展开当前节点。 */
function onRowClick(row: VisibleRow): void {
  emit('navigate', row.node.path)

  if (!row.expanded) {
    void expandNode(row.node)
  } else if (row.node.error) {
    void loadChildren(row.node)
  }
}

/** 刷新按钮：清空缓存后重建根节点，并重新定位当前目录。 */
function reload(): void {
  buildTree()
  const target = props.currentPath || props.homePath
  if (target) {
    void revealPath(target)
  }
}

/** 扁平化：只渲染展开节点的子目录，隐藏的子树不产生 DOM。 */
const visibleRows = computed<VisibleRow[]>(() => {
  const rows: VisibleRow[] = []

  const walk = (nodes: TreeNode[], depth: number): void => {
    for (const node of nodes) {
      const expanded = expandedPaths.value.has(node.path)
      const hasChildren = !node.loaded || (node.children?.length ?? 0) > 0
      rows.push({ node, depth, expanded, hasChildren })

      if (expanded && node.children && node.children.length > 0) {
        walk(node.children, depth + 1)
      }
    }
  }

  walk(rootNodes.value, 0)
  return rows
})

watch(
  () => `${props.roots.join('\n')}\u0000${props.homePath}`,
  () => {
    buildTree()
    const target = props.currentPath || props.homePath
    if (target) {
      void revealPath(target)
    }
  },
  { immediate: true },
)

watch(
  () => props.currentPath,
  (path) => {
    if (path) {
      void revealPath(path)
    }
  },
)
</script>

<template>
  <div class="directory-tree">
    <div class="tree-header">
      <span class="tree-title">目录树</span>
      <el-tooltip content="刷新目录树" placement="bottom">
        <el-button
          class="tree-refresh"
          :icon="Refresh"
          circle
          size="small"
          aria-label="刷新目录树"
          @click="reload"
        />
      </el-tooltip>
    </div>

    <div class="tree-body">
      <el-empty
        v-if="visibleRows.length === 0"
        :image-size="40"
        description="暂无可浏览的目录"
      />

      <div
        v-for="row in visibleRows"
        :key="row.node.path"
        :ref="(element) => setRowRef(row.node.path, element)"
        class="tree-row"
        :class="{ active: row.node.path === currentPath }"
        :style="{ paddingLeft: `${8 + row.depth * 14}px` }"
        :title="row.node.error ? `${row.node.path}（${row.node.error}）` : row.node.path"
        @click="onRowClick(row)"
      >
        <span
          class="tree-arrow"
          :class="{ expanded: row.expanded, hidden: !row.hasChildren }"
          @click.stop="toggleNode(row)"
        >
          <el-icon><ArrowRight /></el-icon>
        </span>
        <el-icon class="tree-folder"><Folder /></el-icon>
        <span class="tree-name">{{ row.node.name }}</span>
        <span v-if="row.node.loading" class="tree-loading">加载中…</span>
        <span v-else-if="row.node.error" class="tree-error">加载失败</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.directory-tree {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.tree-header {
  display: flex;
  flex: none;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-bottom: 1px solid #ebeef5;
}

.tree-title {
  flex: 1;
  color: #303133;
  font-size: 13px;
  font-weight: 600;
}

.tree-refresh {
  flex: none;
}

.tree-body {
  flex: 1;
  min-height: 0;
  padding: 4px;
  overflow-y: auto;
}

.tree-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border-radius: 4px;
  color: #303133;
  font-size: 13px;
  user-select: none;
  cursor: pointer;
}

.tree-row:hover {
  background: #f5f7fa;
  color: #409eff;
}

.tree-row.active {
  background: #ecf5ff;
  color: #409eff;
}

.tree-arrow {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  color: #909399;
  transition: transform 0.15s;
}

.tree-arrow.expanded {
  transform: rotate(90deg);
}

.tree-arrow.hidden {
  visibility: hidden;
}

.tree-folder {
  flex: none;
  color: #e6a23c;
}

.tree-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tree-loading {
  flex: none;
  color: #909399;
  font-size: 12px;
}

.tree-error {
  flex: none;
  color: #f56c6c;
  font-size: 12px;
}
</style>
