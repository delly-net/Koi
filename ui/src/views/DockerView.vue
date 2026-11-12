<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, MoreFilled, Plus, Refresh } from '@element-plus/icons-vue'
import {
  containerAction,
  getContainerLogs,
  getDockerStats,
  getDockerStatus,
  inspectContainer,
  listContainers,
  listImages,
  pruneImages,
  pullImage,
  removeContainer,
  removeImage,
  tagImage,
  type DockerAction,
  type DockerContainer,
  type DockerContainerDetail,
  type DockerImage,
  type DockerStatsEntry,
  type DockerStatus,
} from '@/api/docker'
import { ApiError } from '@/api/http'

type DockerTab = 'containers' | 'images' | 'system'

const activeTab = ref<DockerTab>('containers')

const status = ref<DockerStatus | null>(null)
const containers = ref<DockerContainer[]>([])
const images = ref<DockerImage[]>([])
const stats = ref<DockerStatsEntry[]>([])

const statusLoading = ref(false)
const containersLoading = ref(false)
const imagesLoading = ref(false)
const statsLoading = ref(false)

const forbidden = ref(false)
const forbiddenDetail = ref('')
const unavailable = ref(false)
const errorMessage = ref('')
const lastUpdated = ref('')
const busyId = ref('')

const refreshing = computed(
  () => statusLoading.value || containersLoading.value || imagesLoading.value || statsLoading.value,
)

/** 加载失败提示：403（非 docker 组成员）与 503（docker 不可用）单独展示。 */
function handleLoadError(error: unknown, fallback: string): void {
  if (error instanceof ApiError) {
    if (error.status === 403 && error.message === '无权访问容器管理') {
      forbidden.value = true
      forbiddenDetail.value = error.detail ?? ''
    } else if (error.status === 503) {
      unavailable.value = true
    }

    errorMessage.value = error.detail ? `${error.message}：${error.detail}` : error.message
    return
  }

  errorMessage.value = error instanceof Error ? error.message : fallback
}

/** 任一请求成功即说明权限与 Docker 可用性正常，清除持久提示。 */
function markAvailable(): void {
  forbidden.value = false
  forbiddenDetail.value = ''
  unavailable.value = false
  errorMessage.value = ''
}

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

async function loadStatus(): Promise<void> {
  statusLoading.value = true
  try {
    status.value = await getDockerStatus()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载 Docker 状态失败')
  } finally {
    statusLoading.value = false
  }
}

async function loadContainers(): Promise<void> {
  containersLoading.value = true
  try {
    containers.value = await listContainers()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载容器列表失败')
  } finally {
    containersLoading.value = false
  }
}

async function loadImages(): Promise<void> {
  imagesLoading.value = true
  try {
    images.value = await listImages()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载镜像列表失败')
  } finally {
    imagesLoading.value = false
  }
}

async function loadStats(): Promise<void> {
  statsLoading.value = true
  try {
    stats.value = await getDockerStats()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载资源占用失败')
  } finally {
    statsLoading.value = false
  }
}

/** 仅手动刷新：刷新当前页签所需数据（状态信息总是刷新）。 */
async function refresh(): Promise<void> {
  await loadStatus()
  if (activeTab.value === 'images') {
    await loadImages()
  } else if (activeTab.value === 'system') {
    await loadStats()
  } else {
    await loadContainers()
  }

  lastUpdated.value = new Date().toLocaleTimeString()
}

watch(activeTab, (tab) => {
  if (tab === 'images' && images.value.length === 0) {
    void loadImages()
  }

  if (tab === 'system') {
    if (stats.value.length === 0) {
      void loadStats()
    }

    if (status.value === null) {
      void loadStatus()
    }
  }
})

// ---------------- 容器操作 ----------------

const canStart = (row: DockerContainer): boolean =>
  row.state === 'exited' || row.state === 'created' || row.state === 'dead'
const canStop = (row: DockerContainer): boolean =>
  row.state === 'running' || row.state === 'paused' || row.state === 'restarting'
const canPause = (row: DockerContainer): boolean => row.state === 'running'
const canUnpause = (row: DockerContainer): boolean => row.state === 'paused'

const ACTION_LABELS: Record<DockerAction, string> = {
  start: '启动',
  stop: '停止',
  restart: '重启',
  pause: '暂停',
  unpause: '恢复',
}

function stateTagType(state: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (state) {
    case 'running':
      return 'success'
    case 'paused':
    case 'restarting':
      return 'warning'
    case 'dead':
      return 'danger'
    case 'exited':
      return 'info'
    default:
      return 'info'
  }
}

async function runAction(row: DockerContainer, action: DockerAction): Promise<void> {
  busyId.value = row.id
  try {
    await containerAction(row.id, action)
    ElMessage.success(`${ACTION_LABELS[action]}容器成功`)
    await loadContainers()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    busyId.value = ''
  }
}

async function deleteContainer(row: DockerContainer): Promise<void> {
  const running = row.state === 'running' || row.state === 'paused'
  const message = running
    ? `容器 ${row.names} 正在运行，删除会强制终止并移除该容器。确认删除？`
    : `确认删除容器 ${row.names}？`

  try {
    await ElMessageBox.confirm(message, '删除容器', {
      type: 'warning',
      confirmButtonText: '删除',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  busyId.value = row.id
  try {
    await removeContainer(row.id, running, false)
    ElMessage.success('容器已删除')
    await loadContainers()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    busyId.value = ''
  }
}

async function onContainerCommand(command: string, row: DockerContainer): Promise<void> {
  switch (command) {
    case 'logs':
      await openLogs(row)
      return
    case 'detail':
      await openDetail(row)
      return
    case 'delete':
      await deleteContainer(row)
      return
    default:
      await runAction(row, command as DockerAction)
  }
}

// ---------------- 容器日志 ----------------

const logsVisible = ref(false)
const logsLoading = ref(false)
const logsContent = ref('')
const logsTail = ref(500)
const logsContainer = ref<DockerContainer | null>(null)

const logsTitle = computed(() =>
  logsContainer.value ? `容器日志：${logsContainer.value.names}` : '容器日志',
)

async function openLogs(row: DockerContainer): Promise<void> {
  logsContainer.value = row
  logsTail.value = 500
  logsContent.value = ''
  logsVisible.value = true
  await loadLogs()
}

async function loadLogs(): Promise<void> {
  const target = logsContainer.value
  if (!target) {
    return
  }

  logsLoading.value = true
  try {
    const response = await getContainerLogs(target.id, logsTail.value, true)
    logsContent.value = response.content || '(暂无日志)'
  } catch (error) {
    showError(error)
  } finally {
    logsLoading.value = false
  }
}

// ---------------- 容器详情 ----------------

const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<DockerContainerDetail | null>(null)

async function openDetail(row: DockerContainer): Promise<void> {
  detail.value = null
  detailVisible.value = true
  detailLoading.value = true
  try {
    detail.value = await inspectContainer(row.id)
  } catch (error) {
    showError(error)
  } finally {
    detailLoading.value = false
  }
}

const detailPortsText = computed(() => {
  if (!detail.value || detail.value.ports.length === 0) {
    return '—'
  }

  return detail.value.ports
    .map((port) =>
      port.hostPort ? `${port.hostIp || '0.0.0.0'}:${port.hostPort} → ${port.containerPort}` : port.containerPort,
    )
    .join('，')
})

const detailMountsText = computed(() => {
  if (!detail.value || detail.value.mounts.length === 0) {
    return '—'
  }

  return detail.value.mounts
    .map((mount) => `${mount.source} → ${mount.destination}${mount.mode ? `（${mount.mode}）` : ''}`)
    .join('；')
})

const detailLabelsText = computed(() => {
  if (!detail.value) {
    return ''
  }

  return Object.entries(detail.value.labels)
    .map(([key, value]) => `${key}=${value}`)
    .join('\n')
})

// ---------------- 镜像操作 ----------------

const pullVisible = ref(false)
const pullReference = ref('')
const pullLoading = ref(false)
const pruneLoading = ref(false)

function openPull(): void {
  pullReference.value = ''
  pullVisible.value = true
}

async function submitPull(): Promise<void> {
  const reference = pullReference.value.trim()
  if (!reference) {
    ElMessage.warning('请输入镜像引用，例如 nginx:latest')
    return
  }

  pullLoading.value = true
  try {
    await pullImage(reference)
    ElMessage.success(`镜像 ${reference} 拉取完成`)
    pullVisible.value = false
    pullReference.value = ''
    await loadImages()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    pullLoading.value = false
  }
}

const tagVisible = ref(false)
const tagLoading = ref(false)
const tagRow = ref<DockerImage | null>(null)
const tagTarget = ref('')

const tagSourceLabel = computed(() => {
  if (!tagRow.value) {
    return ''
  }

  const row = tagRow.value
  const suffix = row.tag && row.tag !== '<none>' ? `:${row.tag}` : ''
  return `${row.repository}${suffix}`
})

function resetTagForm(): void {
  tagRow.value = null
  tagTarget.value = ''
}

function openTag(row: DockerImage): void {
  tagRow.value = row
  tagTarget.value = row.repository && row.repository !== '<none>' ? `${row.repository}:` : ''
  tagVisible.value = true
}

/** 目标引用规范化：末段没有标签时 Docker 默认使用 latest。 */
function normalizeImageReference(reference: string): string {
  const trimmed = reference.trim()
  const lastSegment = trimmed.slice(trimmed.lastIndexOf('/') + 1)
  return lastSegment.includes(':') ? trimmed : `${trimmed}:latest`
}

/** 提交前在已加载列表中查找同名标签；后端仍会做权威预检。 */
function findImageByReference(reference: string): DockerImage | undefined {
  const normalized = normalizeImageReference(reference)
  return images.value.find((item) => {
    const suffix = item.tag && item.tag !== '<none>' ? `:${item.tag}` : ''
    return `${item.repository}${suffix}` === normalized
  })
}

async function submitTag(): Promise<void> {
  const row = tagRow.value
  const target = tagTarget.value.trim()
  if (!row) {
    return
  }

  if (!target) {
    ElMessage.warning('请输入目标标签，例如 myapp:1.0.0')
    return
  }

  if (target.endsWith(':') || target.endsWith('/') || target.includes('@')) {
    ElMessage.warning('目标标签格式不合法，请使用「仓库名[:标签]」形式')
    return
  }

  if (findImageByReference(target)) {
    ElMessage.error(
      `目标标签 ${normalizeImageReference(target)} 已存在，请先删除旧标签或使用其他标签名`,
    )
    return
  }

  tagLoading.value = true
  try {
    await tagImage(row.id, target)
    ElMessage.success(`已为镜像创建标签 ${normalizeImageReference(target)}`)
    tagVisible.value = false
    resetTagForm()
    await loadImages()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    tagLoading.value = false
  }
}

function onImageCommand(command: string, row: DockerImage): void {
  if (command === 'tag') {
    openTag(row)
    return
  }

  if (command === 'delete') {
    void deleteImage(row)
  }
}

type ImageDeleteOutcome = 'deleted' | 'forced' | 'cancelled'

/** 普通删除；遇到 409 冲突时提示用户确认后以 force 重试。 */
async function removeImageWithForceConfirm(
  row: DockerImage,
  name: string,
): Promise<ImageDeleteOutcome> {
  try {
    await removeImage(row.id, false)
    return 'deleted'
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 409) {
      throw error
    }

    // 运行中容器占用的镜像即使 -f 也无法删除，直接给出处理建议。
    if (error.message === '镜像正在被容器使用') {
      showError(error)
      return 'cancelled'
    }

    const reason = error.detail ?? error.message
    try {
      await ElMessageBox.confirm(
        `镜像 ${name} 无法直接删除：${reason} 强制删除会同时移除该镜像的所有仓库标签，且不可恢复，是否继续？`,
        '强制删除镜像',
        {
          type: 'warning',
          confirmButtonText: '强制删除',
          cancelButtonText: '取消',
        },
      )
    } catch {
      return 'cancelled'
    }

    await removeImage(row.id, true)
    return 'forced'
  }
}

async function deleteImage(row: DockerImage): Promise<void> {
  const suffix = row.tag && row.tag !== '<none>' ? `:${row.tag}` : ''
  const name = `${row.repository}${suffix}`

  try {
    await ElMessageBox.confirm(
      `确认删除镜像 ${name}？若镜像被多个仓库标签或容器引用，将提示是否强制删除。`,
      '删除镜像',
      {
        type: 'warning',
        confirmButtonText: '删除',
        cancelButtonText: '取消',
      },
    )
  } catch {
    return
  }

  busyId.value = row.id
  try {
    const outcome = await removeImageWithForceConfirm(row, name)
    if (outcome === 'cancelled') {
      return
    }

    ElMessage.success(outcome === 'forced' ? '镜像已强制删除' : '镜像已删除')
    await loadImages()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    busyId.value = ''
  }
}

async function pruneDanglingImages(): Promise<void> {
  try {
    await ElMessageBox.confirm('将删除所有未被任何镜像引用的悬空镜像，确认继续？', '清理悬空镜像', {
      type: 'warning',
      confirmButtonText: '清理',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  pruneLoading.value = true
  try {
    await pruneImages()
    ElMessage.success('悬空镜像已清理')
    await loadImages()
    await loadStatus()
  } catch (error) {
    showError(error)
  } finally {
    pruneLoading.value = false
  }
}

// ---------------- 展示辅助 ----------------

function formatBytes(value: number): string {
  if (!value) {
    return '—'
  }

  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  let size = value
  let index = 0
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024
    index += 1
  }

  return `${size.toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

function shortId(value: string): string {
  return value.length > 12 ? value.slice(0, 12) : value
}

onMounted(() => {
  void refresh()
})
</script>

<template>
  <div class="docker-view">
    <div class="toolbar">
      <div class="toolbar-title">
        <span class="title">容器管理</span>
        <span class="hint">
          通过 docker 命令查看与管理容器、镜像与运行状态；数据仅在手动刷新时更新。
          Docker 操作以服务账号执行，等同于宿主 root 权限。
        </span>
      </div>
      <div class="actions">
        <span v-if="lastUpdated" class="updated">更新于 {{ lastUpdated }}</span>
        <el-button :icon="Refresh" :loading="refreshing" @click="refresh">刷新</el-button>
      </div>
    </div>

    <el-alert
      v-if="forbidden"
      class="notice"
      type="error"
      show-icon
      :closable="false"
      title="当前账号无权访问容器管理"
      :description="forbiddenDetail || '请将账号加入 docker 组后重新登录。'"
    />
    <el-alert
      v-else-if="unavailable"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="Docker 不可用"
      :description="errorMessage || '请确认 Docker 已安装并启动。'"
    />
    <el-alert
      v-else-if="errorMessage"
      class="notice"
      type="error"
      show-icon
      :closable="false"
      title="加载失败"
      :description="errorMessage"
    />

    <el-tabs v-model="activeTab" class="docker-tabs">
      <el-tab-pane label="容器" name="containers">
        <div class="tab-pane-body">
          <div class="pane-toolbar">
            <span class="hint">共 {{ containers.length }} 个容器（含已停止）</span>
          </div>

          <div class="table-wrap">
            <el-table
              v-loading="containersLoading"
              :data="containers"
              height="100%"
              border
              stripe
              empty-text="暂无容器"
            >
              <el-table-column label="名称" min-width="200">
                <template #default="{ row }">
                  <span class="container-name" @click="openDetail(row)">
                    {{ row.names || shortId(row.id) }}
                  </span>
                </template>
              </el-table-column>
              <el-table-column prop="image" label="镜像" min-width="220" show-overflow-tooltip />
              <el-table-column label="状态" width="170">
                <template #default="{ row }">
                  <el-tag :type="stateTagType(row.state)" size="small" effect="plain">
                    {{ row.state }}
                  </el-tag>
                  <div class="status-text">{{ row.status }}</div>
                </template>
              </el-table-column>
              <el-table-column prop="ports" label="端口" min-width="180" show-overflow-tooltip />
              <el-table-column prop="runningFor" label="创建" width="150" show-overflow-tooltip />
              <el-table-column label="ID" width="130">
                <template #default="{ row }">{{ shortId(row.id) }}</template>
              </el-table-column>
              <el-table-column label="操作" width="96" fixed="right" align="center">
                <template #default="{ row }">
                  <el-dropdown
                    trigger="click"
                    @command="(command: string) => onContainerCommand(command, row)"
                  >
                    <el-button size="small" :icon="MoreFilled" :loading="busyId === row.id">
                      操作
                    </el-button>
                    <template #dropdown>
                      <el-dropdown-menu>
                        <el-dropdown-item command="start" :disabled="!canStart(row)">
                          启动
                        </el-dropdown-item>
                        <el-dropdown-item command="stop" :disabled="!canStop(row)">
                          停止
                        </el-dropdown-item>
                        <el-dropdown-item command="restart">重启</el-dropdown-item>
                        <el-dropdown-item command="pause" :disabled="!canPause(row)">
                          暂停
                        </el-dropdown-item>
                        <el-dropdown-item command="unpause" :disabled="!canUnpause(row)">
                          恢复
                        </el-dropdown-item>
                        <el-dropdown-item command="logs" divided>日志</el-dropdown-item>
                        <el-dropdown-item command="detail">详情</el-dropdown-item>
                        <el-dropdown-item command="delete" divided class="danger-item">
                          删除
                        </el-dropdown-item>
                      </el-dropdown-menu>
                    </template>
                  </el-dropdown>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane label="镜像" name="images">
        <div class="tab-pane-body">
          <div class="pane-toolbar">
            <span class="hint">共 {{ images.length }} 个镜像</span>
            <div class="actions">
              <el-button :icon="Plus" size="small" @click="openPull">拉取镜像</el-button>
              <el-button
                :icon="Delete"
                size="small"
                :loading="pruneLoading"
                @click="pruneDanglingImages"
              >
                清理悬空镜像
              </el-button>
            </div>
          </div>

          <div class="table-wrap">
            <el-table
              v-loading="imagesLoading"
              :data="images"
              height="100%"
              border
              stripe
              empty-text="暂无镜像"
            >
              <el-table-column prop="repository" label="仓库" min-width="220" show-overflow-tooltip />
              <el-table-column prop="tag" label="标签" width="130" />
              <el-table-column label="镜像 ID" width="150">
                <template #default="{ row }">{{ shortId(row.id.replace('sha256:', '')) }}</template>
              </el-table-column>
              <el-table-column prop="size" label="大小" width="110" />
              <el-table-column prop="createdAt" label="创建时间" width="200" show-overflow-tooltip />
              <el-table-column label="操作" width="96" fixed="right" align="center">
                <template #default="{ row }">
                  <el-dropdown
                    trigger="click"
                    @command="(command: string) => onImageCommand(command, row)"
                  >
                    <el-button size="small" :icon="MoreFilled" :loading="busyId === row.id">
                      操作
                    </el-button>
                    <template #dropdown>
                      <el-dropdown-menu>
                        <el-dropdown-item command="tag">打标签</el-dropdown-item>
                        <el-dropdown-item command="delete" divided class="danger-item">
                          删除
                        </el-dropdown-item>
                      </el-dropdown-menu>
                    </template>
                  </el-dropdown>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane label="系统" name="system">
        <div class="system-pane">
          <el-descriptions
            v-loading="statusLoading"
            title="Docker 引擎"
            :column="2"
            border
            class="engine-info"
          >
            <el-descriptions-item label="客户端版本">
              {{ status?.clientVersion || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="服务端版本">
              {{ status?.serverVersion || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="API 版本">
              {{ status?.apiVersion || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="存储驱动">
              {{ status?.driver || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="操作系统">
              {{ status?.operatingSystem || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="架构">
              {{ status?.architecture || '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="CPU">
              {{ status?.cpus ?? '—' }}
            </el-descriptions-item>
            <el-descriptions-item label="内存">
              {{ formatBytes(status?.memoryTotal ?? 0) }}
            </el-descriptions-item>
            <el-descriptions-item label="容器">
              {{ status?.containers ?? 0 }}（运行 {{ status?.containersRunning ?? 0 }} / 暂停
              {{ status?.containersPaused ?? 0 }} / 停止 {{ status?.containersStopped ?? 0 }}）
            </el-descriptions-item>
            <el-descriptions-item label="镜像">
              {{ status?.images ?? 0 }}
            </el-descriptions-item>
          </el-descriptions>

          <div class="pane-toolbar">
            <span class="section-title">资源占用（docker stats 快照）</span>
            <div class="actions">
              <el-button size="small" :icon="Refresh" :loading="statsLoading" @click="loadStats">
                刷新
              </el-button>
            </div>
          </div>

          <el-table
            v-loading="statsLoading"
            :data="stats"
            border
            stripe
            empty-text="没有运行中的容器"
          >
            <el-table-column prop="name" label="名称" min-width="180" />
            <el-table-column label="ID" width="140">
              <template #default="{ row }">{{ shortId(row.id) }}</template>
            </el-table-column>
            <el-table-column prop="cpuPercent" label="CPU" width="100" />
            <el-table-column prop="memUsage" label="内存" width="170" />
            <el-table-column prop="memPercent" label="内存占比" width="110" />
            <el-table-column prop="netIo" label="网络 I/O" width="180" />
            <el-table-column prop="blockIo" label="块 I/O" width="180" />
            <el-table-column prop="pids" label="PID" width="90" />
          </el-table>
        </div>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="logsVisible" :title="logsTitle" width="72%" top="6vh">
      <div class="logs-toolbar">
        <el-select v-model="logsTail" style="width: 130px" @change="loadLogs">
          <el-option :value="100" label="最近 100 行" />
          <el-option :value="500" label="最近 500 行" />
          <el-option :value="2000" label="最近 2000 行" />
          <el-option :value="5000" label="最近 5000 行" />
        </el-select>
        <el-button :icon="Refresh" size="small" :loading="logsLoading" @click="loadLogs">
          刷新日志
        </el-button>
        <span class="hint">快照模式，不会自动跟踪新日志</span>
      </div>
      <pre v-loading="logsLoading" class="logs-content">{{ logsContent }}</pre>
    </el-dialog>

    <el-drawer v-model="detailVisible" title="容器详情" size="600px">
      <div v-loading="detailLoading">
        <el-descriptions v-if="detail" :column="1" border size="small">
          <el-descriptions-item label="名称">{{ detail.name }}</el-descriptions-item>
          <el-descriptions-item label="ID">{{ detail.id }}</el-descriptions-item>
          <el-descriptions-item label="镜像">{{ detail.image }}</el-descriptions-item>
          <el-descriptions-item label="状态">
            <el-tag :type="stateTagType(detail.status)" size="small" effect="plain">
              {{ detail.status }}
            </el-tag>
            <span v-if="detail.exitCode !== 0" class="status-text inline">
              退出码 {{ detail.exitCode }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="创建时间">{{ detail.created }}</el-descriptions-item>
          <el-descriptions-item label="启动时间">{{ detail.startedAt }}</el-descriptions-item>
          <el-descriptions-item label="结束时间">{{ detail.finishedAt }}</el-descriptions-item>
          <el-descriptions-item label="重启次数">{{ detail.restartCount }}</el-descriptions-item>
          <el-descriptions-item label="启动命令">
            {{ detail.command.join(' ') || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="Entrypoint">
            {{ detail.entrypoint.join(' ') || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="端口">{{ detailPortsText }}</el-descriptions-item>
          <el-descriptions-item label="挂载">{{ detailMountsText }}</el-descriptions-item>
        </el-descriptions>

        <el-empty v-else-if="!detailLoading" description="无法加载容器详情" />

        <div v-if="detailLabelsText" class="detail-labels">
          <div class="section-title">标签</div>
          <pre class="detail-block">{{ detailLabelsText }}</pre>
        </div>
      </div>
    </el-drawer>

    <el-dialog v-model="pullVisible" title="拉取镜像" width="480px">
      <el-input
        v-model="pullReference"
        placeholder="例如 nginx:latest 或 registry.example.com/team/app:1.0"
        maxlength="255"
        @keyup.enter="submitPull"
      />
      <template #footer>
        <el-button @click="pullVisible = false">取消</el-button>
        <el-button type="primary" :loading="pullLoading" @click="submitPull">拉取</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="tagVisible" title="打标签" width="520px" @closed="resetTagForm">
      <el-form label-width="80px">
        <el-form-item label="源镜像">
          <span class="tag-source">{{ tagSourceLabel || '—' }}</span>
        </el-form-item>
        <el-form-item label="目标标签">
          <el-input
            v-model="tagTarget"
            placeholder="例如 myapp:1.0.0"
            maxlength="255"
            clearable
            @keyup.enter="submitTag"
          />
        </el-form-item>
      </el-form>
      <div class="hint">
        格式为「仓库名[:标签]」，省略标签时 Docker 默认使用 latest；目标标签已存在时会被拒绝，请先删除旧标签。
      </div>
      <template #footer>
        <el-button @click="tagVisible = false">取消</el-button>
        <el-button type="primary" :loading="tagLoading" @click="submitTag">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.docker-view {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 16px;
  overflow: hidden;
}

.toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 16px;
  margin-bottom: 12px;
}

.toolbar-title {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
  gap: 4px;
}

.title {
  font-size: 16px;
  font-weight: 600;
}

.hint {
  color: #8a919f;
  font-size: 12px;
}

.actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
}

.updated {
  color: #8a919f;
  font-size: 12px;
}

.notice {
  flex: none;
  margin-bottom: 12px;
}

/* 页签内容区占满剩余高度，表格在内部滚动 */
.docker-tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.docker-tabs :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: 8px;
}

.docker-tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.docker-tabs :deep(.el-tab-pane) {
  height: 100%;
}

.tab-pane-body {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.pane-toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.pane-toolbar .actions {
  margin-left: auto;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.container-name {
  color: #409eff;
  cursor: pointer;
}

.container-name:hover {
  text-decoration: underline;
}

.status-text {
  margin-top: 2px;
  color: #8a919f;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.status-text.inline {
  margin: 0 0 0 8px;
  display: inline;
}

.system-pane {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
  overflow: auto;
  padding-right: 4px;
}

.engine-info {
  flex: none;
}

.section-title {
  font-size: 14px;
  font-weight: 600;
}

.logs-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.logs-content {
  box-sizing: border-box;
  max-height: 62vh;
  margin: 0;
  padding: 12px;
  overflow: auto;
  background: #1e1e1e;
  border-radius: 6px;
  color: #d4d4d4;
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-all;
}

.detail-labels {
  margin-top: 16px;
}

.tag-source {
  color: #303133;
  font-family: Consolas, 'Courier New', monospace;
  word-break: break-all;
}

.detail-block {
  box-sizing: border-box;
  max-height: 240px;
  margin: 8px 0 0;
  padding: 10px;
  overflow: auto;
  background: #f5f7fa;
  border-radius: 6px;
  color: #606266;
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
}

:global(.el-dropdown-menu__item.danger-item) {
  color: #f56c6c;
}
</style>
