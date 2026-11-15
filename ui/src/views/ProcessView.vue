<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { MoreFilled, Refresh, Setting } from '@element-plus/icons-vue'
import {
  getProcess,
  getProcessSettings,
  listProcesses,
  saveProcessSettings,
  terminateProcess,
  type ProcessDetail,
  type ProcessSettings,
  type ProcessSummary,
} from '@/api/processes'
import { ApiError } from '@/api/http'

/** active 由工作区传入：仅进程管理标签可见时才自动刷新进程列表。 */
const props = withDefaults(defineProps<{ active?: boolean }>(), { active: true })

const processes = ref<ProcessSummary[]>([])
const total = ref(0)
const truncated = ref(false)
const loading = ref(false)
const keyword = ref('')
const userFilter = ref('all')
const lastUpdated = ref('')
const refreshIntervalSeconds = ref(5)

const forbidden = ref(false)
const forbiddenDetail = ref('')
const unavailable = ref(false)
const errorMessage = ref('')

// ---------------- 进程管理设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<ProcessSettings | null>(null)
const settingsForm = reactive({
  enabled: true,
  allowedGroups: [] as string[],
  maxProcesses: 2000,
  maxCommandLineLength: 512,
  sampleWindowMilliseconds: 200,
  terminateTimeoutSeconds: 5,
  refreshIntervalSeconds: 5,
})
const groupSuggestions = ['sudo', 'wheel']

const filteredProcesses = computed(() => {
  const text = keyword.value.trim().toLowerCase()
  return processes.value.filter((process) => {
    if (userFilter.value !== 'all' && process.user !== userFilter.value) {
      return false
    }

    if (!text) {
      return true
    }

    return (
      String(process.pid) === text ||
      process.name.toLowerCase().includes(text) ||
      process.user.toLowerCase().includes(text) ||
      process.commandLine.toLowerCase().includes(text)
    )
  })
})

const userOptions = computed(() => {
  const users = new Set(processes.value.map((process) => process.user))
  return Array.from(users).sort((left, right) => left.localeCompare(right))
})

/** 加载失败提示：403（非允许组）与 503（非 Linux / 已关闭）单独展示。 */
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

/** 任一请求成功即说明权限与平台正常，清除持久提示。 */
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

// ---------------- 列表与自动刷新 ----------------

/** 防止自动刷新与手动刷新叠加触发。 */
let requestInFlight = false

async function loadProcesses(silent = false): Promise<void> {
  if (requestInFlight) {
    return
  }

  requestInFlight = true
  if (!silent) {
    loading.value = true
  }

  try {
    const response = await listProcesses()
    processes.value = response.processes
    total.value = response.total
    truncated.value = response.truncated
    refreshIntervalSeconds.value = response.refreshIntervalSeconds || 5
    lastUpdated.value = new Date(response.sampledAt).toLocaleTimeString()
    markAvailable()
  } catch (error) {
    // 失败时保留上一次快照，只展示常驻提示。
    handleLoadError(error, '加载进程列表失败')
  } finally {
    requestInFlight = false
    if (!silent) {
      loading.value = false
    }
  }
}

/** 手动刷新：保留显式的加载反馈。 */
async function refresh(): Promise<void> {
  await loadProcesses()
}

const autoRefresh = ref(true)
let timer: number | undefined

function canPoll(): boolean {
  return props.active && autoRefresh.value && document.visibilityState === 'visible'
}

function stopTimer(): void {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

function startTimer(): void {
  stopTimer()
  if (!canPoll()) {
    return
  }

  const interval = Math.max(1, refreshIntervalSeconds.value) * 1000
  timer = window.setInterval(() => {
    void loadProcesses(true)
  }, interval)
}

/** 切换页签或设置后重新计时，避免沿用旧的刷新间隔。 */
function restartTimer(): void {
  startTimer()
}

function onVisibilityChange(): void {
  if (canPoll()) {
    void loadProcesses(true)
    restartTimer()
    return
  }

  stopTimer()
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void loadProcesses(true)
      restartTimer()
      return
    }

    stopTimer()
  },
)

watch(autoRefresh, (enabled) => {
  if (enabled) {
    void loadProcesses(true)
    restartTimer()
    return
  }

  stopTimer()
})

watch(refreshIntervalSeconds, restartTimer)

// ---------------- 结束进程 ----------------

const busyPid = ref(0)

const canTerminate = (row: ProcessSummary): boolean => row.pid > 1

async function confirmTerminate(row: ProcessSummary, force: boolean): Promise<void> {
  const label = force ? '强制结束' : '结束'
  const consequence = force
    ? '将发送 SIGKILL，进程没有机会保存数据或清理资源，可能导致数据丢失。'
    : '将发送 SIGTERM，进程有机会正常退出；若在等待时间内未退出，可再选择强制结束。'

  try {
    await ElMessageBox.confirm(
      `确认${label}进程 ${row.name}（PID ${row.pid}）？${consequence}若结束的是 Koi 服务自身进程，当前页面会立即中断（后端已禁止）。`,
      `${label}进程`,
      {
        type: 'warning',
        confirmButtonText: label,
        cancelButtonText: '取消',
      },
    )
  } catch {
    return
  }

  await runTerminate(row, force)
}

async function runTerminate(row: ProcessSummary, force: boolean): Promise<void> {
  busyPid.value = row.pid
  try {
    const result = await terminateProcess(row.pid, force)
    ElMessage.success(
      `已发送 ${result.signal}，进程 ${row.pid} 已退出（等待 ${result.waitedMilliseconds} 毫秒）`,
    )
    await loadProcesses(true)
  } catch (error) {
    // 优雅结束超时：引导用户确认后强制结束。
    if (error instanceof ApiError && error.status === 409 && !force) {
      try {
        await ElMessageBox.confirm(
          `进程 ${row.name}（PID ${row.pid}）未在限期内退出：${error.detail ?? '可选择强制结束。'}`,
          '强制结束进程',
          {
            type: 'warning',
            confirmButtonText: '强制结束（SIGKILL）',
            cancelButtonText: '取消',
          },
        )
      } catch {
        return
      }

      await runTerminate(row, true)
      return
    }

    showError(error)
  } finally {
    busyPid.value = 0
  }
}

async function onCommand(command: string, row: ProcessSummary): Promise<void> {
  if (command === 'detail') {
    await openDetail(row)
    return
  }

  await confirmTerminate(row, command === 'kill')
}

// ---------------- 进程详情 ----------------

const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<ProcessDetail | null>(null)

async function openDetail(row: ProcessSummary): Promise<void> {
  detail.value = null
  detailVisible.value = true
  detailLoading.value = true
  try {
    detail.value = await getProcess(row.pid)
  } catch (error) {
    showError(error)
  } finally {
    detailLoading.value = false
  }
}

function stateText(state: string): string {
  switch (state) {
    case 'R':
      return '运行中'
    case 'S':
      return '睡眠'
    case 'D':
      return '磁盘等待'
    case 'Z':
      return '僵尸'
    case 'T':
      return '已停止'
    case 't':
      return '跟踪停止'
    case 'X':
      return '已死亡'
    case 'I':
      return '空闲'
    default:
      return state || '—'
  }
}

function stateTagType(state: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (state) {
    case 'R':
      return 'success'
    case 'D':
      return 'warning'
    case 'Z':
    case 'X':
      return 'danger'
    default:
      return 'info'
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

function formatRuntime(seconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(seconds))
  if (totalSeconds < 60) {
    return `${totalSeconds} 秒`
  }

  const days = Math.floor(totalSeconds / 86400)
  const hours = Math.floor((totalSeconds % 86400) / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const remainder = totalSeconds % 60
  const clock = [hours, minutes, remainder]
    .map((part) => String(part).padStart(2, '0'))
    .join(':')

  return days > 0 ? `${days} 天 ${clock}` : clock
}

// ---------------- 进程管理设置（仅 root） ----------------

function applySettingsForm(value: ProcessSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.allowedGroups = [...value.allowedGroups]
  settingsForm.maxProcesses = value.maxProcesses
  settingsForm.maxCommandLineLength = value.maxCommandLineLength
  settingsForm.sampleWindowMilliseconds = value.sampleWindowMilliseconds
  settingsForm.terminateTimeoutSeconds = value.terminateTimeoutSeconds
  settingsForm.refreshIntervalSeconds = value.refreshIntervalSeconds
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getProcessSettings()
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

async function saveSettings(): Promise<void> {
  settingsSaving.value = true
  try {
    const saved = await saveProcessSettings({
      enabled: settingsForm.enabled,
      allowedGroups: settingsForm.allowedGroups
        .map((group) => group.trim())
        .filter((group) => group.length > 0),
      maxProcesses: settingsForm.maxProcesses,
      maxCommandLineLength: settingsForm.maxCommandLineLength,
      sampleWindowMilliseconds: settingsForm.sampleWindowMilliseconds,
      terminateTimeoutSeconds: settingsForm.terminateTimeoutSeconds,
      refreshIntervalSeconds: settingsForm.refreshIntervalSeconds,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    ElMessage.success('设置已保存并立即生效')
    await loadProcesses()
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

onMounted(() => {
  void loadProcesses()
  // 静默探测设置权限，与列表加载并行；非 root 403 / 非 Linux 503 时入口保持隐藏。
  void loadSettingsAvailability()
  document.addEventListener('visibilitychange', onVisibilityChange)
  restartTimer()
})

onUnmounted(() => {
  stopTimer()
  document.removeEventListener('visibilitychange', onVisibilityChange)
})
</script>

<template>
  <div class="process-view">
    <div class="toolbar">
      <div class="toolbar-title">
        <span class="title">进程管理</span>
        <span class="hint">
          读取 Linux /proc 展示进程 CPU/内存占用与命令行；CPU 占比以单核为 100%。结束进程等同于向任意进程发送信号，仅允许管理员组使用。
        </span>
      </div>
      <div class="actions">
        <el-switch v-model="autoRefresh" active-text="自动刷新" />
        <el-tooltip v-if="settingsAvailable" content="进程管理设置" placement="bottom">
          <el-button
            :icon="Setting"
            circle
            :loading="settingsLoading"
            aria-label="进程管理设置"
            @click="openSettings"
          />
        </el-tooltip>
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
      title="当前账号无权访问进程管理"
      :description="forbiddenDetail || '请将账号加入允许的管理员组（sudo / wheel）后重新登录。'"
    />
    <el-alert
      v-else-if="unavailable"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="进程管理不可用"
      :description="errorMessage || '请确认系统为 Linux 且 /proc 已挂载。'"
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
        placeholder="按 PID、进程名、用户或命令行过滤"
        clearable
      />
      <el-select v-model="userFilter" class="filter-select">
        <el-option label="全部用户" value="all" />
        <el-option v-for="user in userOptions" :key="user" :label="user" :value="user" />
      </el-select>
      <span class="hint">
        共 {{ filteredProcesses.length }} / {{ total }} 个进程（按 CPU 降序{{
          truncated ? '，已按最大进程数截断' : ''
        }}）
      </span>
    </div>

    <div class="table-wrap">
      <el-table
        v-loading="loading"
        :data="filteredProcesses"
        height="100%"
        border
        stripe
        empty-text="没有匹配的进程"
        :default-sort="{ prop: 'cpuPercent', order: 'descending' }"
      >
        <el-table-column prop="pid" label="PID" width="90" sortable />
        <el-table-column prop="user" label="用户" width="120" show-overflow-tooltip />
        <el-table-column label="进程名" width="180">
          <template #default="{ row }">
            <span class="process-name" @click="openDetail(row)">{{ row.name }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="commandLine" label="命令行" min-width="260" show-overflow-tooltip />
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="stateTagType(row.state)" size="small" effect="plain">
              {{ stateText(row.state) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="cpuPercent" label="CPU%" width="100" sortable>
          <template #default="{ row }">{{ row.cpuPercent.toFixed(1) }}%</template>
        </el-table-column>
        <el-table-column prop="memoryPercent" label="内存%" width="100" sortable>
          <template #default="{ row }">{{ row.memoryPercent.toFixed(1) }}%</template>
        </el-table-column>
        <el-table-column prop="rssBytes" label="常驻内存" width="120" sortable>
          <template #default="{ row }">{{ formatBytes(row.rssBytes) }}</template>
        </el-table-column>
        <el-table-column prop="threads" label="线程" width="80" sortable />
        <el-table-column prop="runtimeSeconds" label="运行时长" width="130" sortable>
          <template #default="{ row }">{{ formatRuntime(row.runtimeSeconds) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="96" fixed="right" align="center">
          <template #default="{ row }">
            <el-dropdown trigger="click" @command="(command: string) => onCommand(command, row)">
              <el-button size="small" :icon="MoreFilled" :loading="busyPid === row.pid">
                操作
              </el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="detail">详情</el-dropdown-item>
                  <el-dropdown-item command="terminate" divided :disabled="!canTerminate(row)">
                    结束进程
                  </el-dropdown-item>
                  <el-dropdown-item command="kill" :disabled="!canTerminate(row)">
                    强制结束
                  </el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-drawer v-model="detailVisible" title="进程详情" size="640px">
      <div v-loading="detailLoading">
        <el-descriptions v-if="detail" :column="1" border size="small">
          <el-descriptions-item label="PID">{{ detail.pid }}</el-descriptions-item>
          <el-descriptions-item label="父进程 PID">{{ detail.parentPid }}</el-descriptions-item>
          <el-descriptions-item label="进程名">{{ detail.name }}</el-descriptions-item>
          <el-descriptions-item label="用户">
            {{ detail.user }}（uid {{ detail.uid }}）
          </el-descriptions-item>
          <el-descriptions-item label="状态">
            <el-tag :type="stateTagType(detail.state)" size="small" effect="plain">
              {{ stateText(detail.state) }}
            </el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="CPU 占用">
            {{ detail.cpuPercent.toFixed(1) }}%
          </el-descriptions-item>
          <el-descriptions-item label="内存占用">
            {{ detail.memoryPercent.toFixed(1) }}%（常驻 {{ formatBytes(detail.rssBytes) }}，虚拟
            {{ formatBytes(detail.virtualBytes) }}）
          </el-descriptions-item>
          <el-descriptions-item label="线程数">{{ detail.threads }}</el-descriptions-item>
          <el-descriptions-item label="运行时长">
            {{ formatRuntime(detail.runtimeSeconds) }}
          </el-descriptions-item>
          <el-descriptions-item label="可执行文件">
            {{ detail.executablePath || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="工作目录">
            {{ detail.workingDirectory || '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="累计读取">
            {{ formatBytes(detail.readBytes) }}
          </el-descriptions-item>
          <el-descriptions-item label="累计写入">
            {{ formatBytes(detail.writeBytes) }}
          </el-descriptions-item>
          <el-descriptions-item label="文件描述符">
            {{ detail.openFileCount ?? '—' }}
          </el-descriptions-item>
          <el-descriptions-item label="采样时间">
            {{ new Date(detail.sampledAt).toLocaleString() }}
          </el-descriptions-item>
        </el-descriptions>

        <el-empty v-else-if="!detailLoading" description="无法加载进程详情" />

        <div v-if="detail" class="detail-block-wrap">
          <div class="section-title">命令行</div>
          <pre class="detail-block">{{ detail.commandLine || '—' }}</pre>
        </div>
      </div>
    </el-drawer>

    <el-drawer
      v-model="settingsVisible"
      title="进程管理设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 process），保存后立即生效，无需重启服务。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用进程管理">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后所有账号（含 root）都无法使用进程管理接口。</div>
        </el-form-item>
        <el-form-item label="允许访问的用户组">
          <el-select
            v-model="settingsForm.allowedGroups"
            class="full-width"
            multiple
            filterable
            allow-create
            default-first-option
            placeholder="输入组名后按回车添加"
          >
            <el-option v-for="group in groupSuggestions" :key="group" :label="group" :value="group" />
          </el-select>
          <div class="form-hint">留空表示拒绝所有普通账号；root（uid 0）始终可访问。</div>
        </el-form-item>

        <el-divider content-position="left">采集与展示</el-divider>
        <el-form-item label="最大进程数">
          <el-input-number
            v-model="settingsForm.maxProcesses"
            class="full-width"
            :min="100"
            :max="20000"
            :step="100"
            controls-position="right"
          />
          <div class="form-hint">列表按 CPU 降序截断到该条数，超出时响应标记 truncated。</div>
        </el-form-item>
        <el-form-item label="命令行长度上限（字符）">
          <el-input-number
            v-model="settingsForm.maxCommandLineLength"
            class="full-width"
            :min="64"
            :max="4096"
            :step="64"
            controls-position="right"
          />
          <div class="form-hint">列表中的命令行超出该长度时截断并追加省略号。</div>
        </el-form-item>
        <el-form-item label="采样窗口（毫秒）">
          <el-input-number
            v-model="settingsForm.sampleWindowMilliseconds"
            class="full-width"
            :min="50"
            :max="5000"
            :step="50"
            controls-position="right"
          />
          <div class="form-hint">首次请求或距上次采样超过 30 秒时，CPU 占比使用的内部短采样时长。</div>
        </el-form-item>
        <el-form-item label="自动刷新间隔（秒）">
          <el-input-number
            v-model="settingsForm.refreshIntervalSeconds"
            class="full-width"
            :min="1"
            :max="60"
            controls-position="right"
          />
          <div class="form-hint">前端仅在页签激活且页面可见时按该间隔轮询。</div>
        </el-form-item>

        <el-divider content-position="left">操作</el-divider>
        <el-form-item label="结束进程等待时间（秒）">
          <el-input-number
            v-model="settingsForm.terminateTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="60"
            controls-position="right"
          />
          <div class="form-hint">
            发送 SIGTERM 后等待退出的时间；超时后接口返回 409，由用户确认是否发送 SIGKILL。
          </div>
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
.process-view {
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
  width: 320px;
}

.filter-select {
  width: 160px;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.process-name {
  color: #409eff;
  cursor: pointer;
}

.process-name:hover {
  text-decoration: underline;
}

.section-title {
  font-size: 14px;
  font-weight: 600;
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
  margin: 4px 0 16px;
}

.full-width {
  width: 100%;
}

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}
</style>
