<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { MoreFilled, Refresh } from '@element-plus/icons-vue'
import {
  getService,
  getServiceLogs,
  listServices,
  serviceAction,
  type ServiceAction,
  type ServiceDetail,
  type ServiceSummary,
} from '@/api/services'
import { ApiError } from '@/api/http'

type StateFilter = 'all' | 'active' | 'inactive' | 'failed'

const services = ref<ServiceSummary[]>([])
const loading = ref(false)
const busyName = ref('')
const keyword = ref('')
const stateFilter = ref<StateFilter>('all')
const lastUpdated = ref('')

const forbidden = ref(false)
const forbiddenDetail = ref('')
const unavailable = ref(false)
const errorMessage = ref('')

const ACTION_LABELS: Record<ServiceAction, string> = {
  start: '启动',
  stop: '停止',
  restart: '重启',
  reload: '重载',
  enable: '启用自启',
  disable: '禁用自启',
}

const filteredServices = computed(() => {
  const text = keyword.value.trim().toLowerCase()
  return services.value.filter((service) => {
    if (
      text &&
      !service.name.toLowerCase().includes(text) &&
      !service.description.toLowerCase().includes(text)
    ) {
      return false
    }

    switch (stateFilter.value) {
      case 'active':
        return service.activeState === 'active'
      case 'inactive':
        // 已安装但未加载的单元没有运行态，同样归入「已停止」。
        return service.activeState === 'inactive' || service.activeState === ''
      case 'failed':
        return service.activeState === 'failed'
      default:
        return true
    }
  })
})

const runningCount = computed(
  () => services.value.filter((service) => service.activeState === 'active').length,
)
const failedCount = computed(
  () => services.value.filter((service) => service.activeState === 'failed').length,
)

/** 加载失败提示：403（非允许组）与 503（systemd 不可用）单独展示。 */
function handleLoadError(error: unknown, fallback: string): void {
  if (error instanceof ApiError) {
    if (error.status === 403) {
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

/** 任一请求成功即说明权限与 systemd 可用性正常，清除持久提示。 */
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

async function loadServices(): Promise<void> {
  loading.value = true
  try {
    services.value = await listServices()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载服务列表失败')
  } finally {
    loading.value = false
  }
}

/** 仅手动刷新：与容器管理一致，不启动轮询计时器。 */
async function refresh(): Promise<void> {
  await loadServices()
  lastUpdated.value = new Date().toLocaleTimeString()
}

const canStart = (row: ServiceSummary): boolean =>
  row.activeState !== 'active' &&
  row.activeState !== 'activating' &&
  row.activeState !== 'reloading'
const canStop = (row: ServiceSummary): boolean =>
  row.activeState === 'active' ||
  row.activeState === 'activating' ||
  row.activeState === 'reloading'
const canReload = (row: ServiceSummary): boolean => row.activeState === 'active'
const canEnable = (row: ServiceSummary): boolean =>
  !['enabled', 'static', 'masked', 'generated', 'transient', 'alias'].includes(row.unitFileState)
const canDisable = (row: ServiceSummary): boolean => row.unitFileState === 'enabled'

async function runAction(row: ServiceSummary, action: ServiceAction): Promise<void> {
  if (action === 'stop' || action === 'restart' || action === 'disable') {
    const label = ACTION_LABELS[action]
    const consequence =
      action === 'disable'
        ? '禁用后该服务将不再开机自启。'
        : action === 'stop'
          ? '正在运行的服务会被终止。'
          : '服务将重新启动，当前运行实例会被终止。'

    try {
      await ElMessageBox.confirm(
        `确认${label}服务 ${row.name}？${consequence}若该服务为 Koi 自身，操作会中断当前页面。`,
        `${label}服务`,
        {
          type: 'warning',
          confirmButtonText: label,
          cancelButtonText: '取消',
        },
      )
    } catch {
      return
    }
  }

  busyName.value = row.name
  try {
    await serviceAction(row.name, action)
    ElMessage.success(`${ACTION_LABELS[action]}成功`)
    await loadServices()
  } catch (error) {
    showError(error)
  } finally {
    busyName.value = ''
  }
}

async function onCommand(command: string, row: ServiceSummary): Promise<void> {
  if (command === 'logs') {
    await openLogs(row)
    return
  }

  if (command === 'detail') {
    await openDetail(row)
    return
  }

  await runAction(row, command as ServiceAction)
}

function stateTagType(state: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (state) {
    case 'active':
      return 'success'
    case 'activating':
    case 'deactivating':
    case 'reloading':
      return 'warning'
    case 'failed':
      return 'danger'
    default:
      return 'info'
  }
}

function fileStateTagType(state: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (state) {
    case 'enabled':
      return 'success'
    case 'masked':
      return 'danger'
    default:
      return 'info'
  }
}

function fileStateText(state: string): string {
  return state || '—'
}

// ---------------- 服务日志 ----------------

const logsVisible = ref(false)
const logsLoading = ref(false)
const logsName = ref('')
const logsTail = ref(500)
const logsContent = ref('')

const logsTitle = computed(() => `服务日志：${logsName.value}`)

async function openLogs(row: ServiceSummary): Promise<void> {
  logsName.value = row.name
  logsTail.value = 500
  logsContent.value = ''
  logsVisible.value = true
  await loadLogs()
}

async function loadLogs(): Promise<void> {
  if (!logsName.value) {
    return
  }

  logsLoading.value = true
  try {
    const response = await getServiceLogs(logsName.value, logsTail.value)
    logsContent.value = response.content || '(暂无日志)'
  } catch (error) {
    showError(error)
  } finally {
    logsLoading.value = false
  }
}

// ---------------- 服务详情 ----------------

const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<ServiceDetail | null>(null)

async function openDetail(row: ServiceSummary): Promise<void> {
  detail.value = null
  detailVisible.value = true
  detailLoading.value = true
  try {
    detail.value = await getService(row.name)
  } catch (error) {
    showError(error)
  } finally {
    detailLoading.value = false
  }
}

function formatBytes(value: number | null): string {
  if (value === null || value < 0) {
    return '—'
  }

  if (value === 0) {
    return '0 B'
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

function formatPid(value: number | null): string {
  return value === null || value === 0 ? '—' : String(value)
}

onMounted(() => {
  void refresh()
})
</script>

<template>
  <div class="service-view">
    <div class="toolbar">
      <div class="toolbar-title">
        <span class="title">服务管理</span>
        <span class="hint">
          通过 systemctl 查看与管理 systemd 服务；操作以服务账号执行，等同于宿主 root 权限，数据仅在手动刷新时更新。
        </span>
      </div>
      <div class="actions">
        <span v-if="lastUpdated" class="updated">更新于 {{ lastUpdated }}</span>
        <el-button :icon="Refresh" :loading="loading" @click="refresh">刷新</el-button>
      </div>
    </div>

    <el-alert
      v-if="forbidden"
      class="notice"
      type="error"
      show-icon
      :closable="false"
      title="当前账号无权访问服务管理"
      :description="forbiddenDetail || '请将账号加入允许的管理员组（sudo / wheel）后重新登录。'"
    />
    <el-alert
      v-else-if="unavailable"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="systemd 不可用"
      :description="errorMessage || '请确认系统使用 systemd，且 systemctl 可执行。'"
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

    <div class="pane-toolbar">
      <el-input
        v-model="keyword"
        class="filter-input"
        placeholder="按服务名或描述过滤"
        clearable
      />
      <el-select v-model="stateFilter" class="filter-select">
        <el-option label="全部状态" value="all" />
        <el-option label="运行中" value="active" />
        <el-option label="已停止" value="inactive" />
        <el-option label="失败" value="failed" />
      </el-select>
      <span class="hint">
        共 {{ filteredServices.length }} / {{ services.length }} 个服务（运行 {{ runningCount }}，失败
        {{ failedCount }}）
      </span>
    </div>

    <div class="table-wrap">
      <el-table
        v-loading="loading"
        :data="filteredServices"
        height="100%"
        border
        stripe
        empty-text="没有匹配的服务"
      >
        <el-table-column label="服务名称" min-width="240">
          <template #default="{ row }">
            <span class="service-name" @click="openDetail(row)">{{ row.name }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="description" label="描述" min-width="280" show-overflow-tooltip />
        <el-table-column label="运行状态" width="170">
          <template #default="{ row }">
            <el-tag :type="stateTagType(row.activeState)" size="small" effect="plain">
              {{ row.activeState || '—' }}
            </el-tag>
            <div class="status-text">{{ row.subState || '—' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="自启" width="120">
          <template #default="{ row }">
            <el-tag :type="fileStateTagType(row.unitFileState)" size="small" effect="plain">
              {{ fileStateText(row.unitFileState) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="96" fixed="right" align="center">
          <template #default="{ row }">
            <el-dropdown trigger="click" @command="(command: string) => onCommand(command, row)">
              <el-button size="small" :icon="MoreFilled" :loading="busyName === row.name">
                操作
              </el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="start" :disabled="!canStart(row)">启动</el-dropdown-item>
                  <el-dropdown-item command="stop" :disabled="!canStop(row)">停止</el-dropdown-item>
                  <el-dropdown-item command="restart">重启</el-dropdown-item>
                  <el-dropdown-item command="reload" :disabled="!canReload(row)">重载</el-dropdown-item>
                  <el-dropdown-item command="enable" divided :disabled="!canEnable(row)">
                    启用自启
                  </el-dropdown-item>
                  <el-dropdown-item command="disable" :disabled="!canDisable(row)">
                    禁用自启
                  </el-dropdown-item>
                  <el-dropdown-item command="logs" divided>日志</el-dropdown-item>
                  <el-dropdown-item command="detail">详情</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
      </el-table>
    </div>

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

    <el-drawer v-model="detailVisible" title="服务详情" size="640px">
      <div v-loading="detailLoading">
        <el-descriptions v-if="detail" :column="1" border size="small">
          <el-descriptions-item label="服务名">{{ detail.name }}</el-descriptions-item>
          <el-descriptions-item label="描述">{{ detail.description || '—' }}</el-descriptions-item>
          <el-descriptions-item label="加载状态">{{ detail.loadState || '—' }}</el-descriptions-item>
          <el-descriptions-item label="运行状态">
            <el-tag :type="stateTagType(detail.activeState)" size="small" effect="plain">
              {{ detail.activeState || '—' }}
            </el-tag>
            <span class="status-text inline">{{ detail.subState || '—' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="自启状态">
            {{ detail.unitFileState || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="单元文件">{{ detail.fragmentPath || '—' }}</el-descriptions-item>
          <el-descriptions-item label="主进程 PID">
            {{ formatPid(detail.mainPid) }}
          </el-descriptions-item>
          <el-descriptions-item label="管理进程 PID">
            {{ formatPid(detail.execMainPid) }}
          </el-descriptions-item>
          <el-descriptions-item label="启动时间">
            {{ detail.activeEnterTimestamp || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="内存占用">
            {{ formatBytes(detail.memoryCurrent) }}
          </el-descriptions-item>
          <el-descriptions-item label="任务数">
            {{ detail.tasksCurrent ?? '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="重启次数">{{ detail.restartCount }}</el-descriptions-item>
          <el-descriptions-item label="结果">{{ detail.result || '—' }}</el-descriptions-item>
          <el-descriptions-item label="可重载">{{ detail.canReload || '—' }}</el-descriptions-item>
          <el-descriptions-item label="需 daemon-reload">
            {{ detail.needDaemonReload || '—' }}
          </el-descriptions-item>
        </el-descriptions>

        <el-empty v-else-if="!detailLoading" description="无法加载服务详情" />

        <div v-if="detail && detail.execStart" class="detail-block-wrap">
          <div class="section-title">ExecStart</div>
          <pre class="detail-block">{{ detail.execStart }}</pre>
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.service-view {
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

.pane-toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.filter-input {
  width: 280px;
}

.filter-select {
  width: 140px;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.service-name {
  color: #409eff;
  cursor: pointer;
}

.service-name:hover {
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

.detail-block-wrap {
  margin-top: 16px;
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
</style>
