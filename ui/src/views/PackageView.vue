<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Download, Plus, Refresh, Search, Setting, Upload } from '@element-plus/icons-vue'
import {
  addPackageSource,
  cancelPackageJob,
  createPackageJob,
  getPackageJob,
  getPackageSettings,
  getPackageStatus,
  listInstalled,
  listPackageSources,
  listUpdates,
  removePackageSource,
  savePackageSettings,
  searchPackages,
  setPackageSourceEnabled,
  type PackageAction,
  type PackageJob,
  type PackageJobStatus,
  type PackageSearchResult,
  type PackageSettings,
  type PackageSource,
  type PackageStatus,
  type PackageSummary,
  type PackageUpdate,
} from '@/api/packages'
import { ApiError } from '@/api/http'

type PackageTab = 'installed' | 'install' | 'updates' | 'sources'

const activeTab = ref<PackageTab>('installed')

const status = ref<PackageStatus | null>(null)
const statusLoading = ref(false)
const refreshing = ref(false)
const lastUpdated = ref('')

// ---------------- 应用管理设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<PackageSettings | null>(null)
const settingsForm = reactive({
  enabled: true,
  manager: '',
  allowedGroups: [] as string[],
  commandTimeoutSeconds: 120,
  jobTimeoutSeconds: 1800,
  maxOutputMiB: 4,
  maxLogMiB: 2,
  jobRetentionMinutes: 30,
  maxPackagesPerJob: 50,
  maxInstalledEntries: 5000,
})
const managerOptions = [
  { label: '自动检测', value: '' },
  { label: 'apt（Debian / Ubuntu）', value: 'apt' },
  { label: 'dnf（Fedora / RHEL）', value: 'dnf' },
  { label: 'yum（CentOS 7 等）', value: 'yum' },
  { label: 'pacman（Arch Linux）', value: 'pacman' },
  { label: 'zypper（openSUSE）', value: 'zypper' },
]
const groupSuggestions = ['sudo', 'wheel']

const forbidden = ref(false)
const forbiddenDetail = ref('')
const unavailable = ref(false)
const errorMessage = ref('')

// ---------------- 已安装 ----------------
const installed = ref<PackageSummary[]>([])
const installedTotal = ref(0)
const installedTruncated = ref(false)
const installedLoading = ref(false)
const installedLoaded = ref(false)
const installedFilter = ref('')

// ---------------- 安装 ----------------
const searchKeyword = ref('')
const searchResults = ref<PackageSearchResult[]>([])
const searchTruncated = ref(false)
const searchLoading = ref(false)
const searchDone = ref(false)
const manualName = ref('')

// ---------------- 更新 ----------------
const updates = ref<PackageUpdate[]>([])
const updatesLoading = ref(false)
const updatesLoaded = ref(false)

// ---------------- 软件源 ----------------
const sources = ref<PackageSource[]>([])
const sourcesSupported = ref(false)
const sourcesReadOnly = ref(false)
const sourcesLoading = ref(false)
const sourcesLoaded = ref(false)
const busySourceId = ref('')
const sourceDialogVisible = ref(false)
const sourceSaving = ref(false)
const sourceForm = reactive({
  name: '',
  uri: '',
  suite: '',
  components: '',
  gpgCheck: true,
})

// ---------------- 任务日志 ----------------
const jobStarting = ref(false)
const jobDialogVisible = ref(false)
const job = ref<PackageJob | null>(null)
const jobTitle = ref('')
const jobLog = ref('')
const jobOffset = ref(0)
const jobCancelling = ref(false)
const logRef = ref<HTMLElement | null>(null)
const autoScroll = ref(true)
let jobTimer: number | undefined

const managerLabel = computed(() => {
  if (!status.value) {
    return ''
  }

  const version = status.value.version.trim()
  const shortened = version.length > 80 ? `${version.slice(0, 80)}…` : version
  return shortened ? `${status.value.displayName} · ${shortened}` : status.value.displayName
})

const canModify = computed(() => status.value?.canModify ?? false)
const isApt = computed(() => status.value?.manager === 'apt')
const isRpm = computed(() => status.value?.manager === 'dnf' || status.value?.manager === 'yum')
const jobRunning = computed(() => job.value?.status === 'running')
const actionsDisabled = computed(
  () => !canModify.value || jobStarting.value || jobRunning.value,
)

const filteredInstalled = computed(() => {
  const keyword = installedFilter.value.trim().toLowerCase()
  if (!keyword) {
    return installed.value
  }

  return installed.value.filter((item) => item.name.toLowerCase().includes(keyword))
})

const jobDialogTitle = computed(() =>
  jobTitle.value ? `执行日志：${jobTitle.value}` : '执行日志',
)

/** 加载失败提示：403（非允许组）与 503（包管理器不可用）单独展示。 */
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

/** 任一请求成功即说明权限与包管理器可用性正常，清除持久提示。 */
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
    status.value = await getPackageStatus()
    markAvailable()
  } catch (error) {
    // 刷新失败时保留上次状态，避免整个页面突然变为空态。
    handleLoadError(error, '加载包管理器状态失败')
  } finally {
    statusLoading.value = false
  }
}

function bytesToMiB(bytes: number): number {
  return Math.max(1, Math.round(bytes / (1024 * 1024)))
}

function applySettingsForm(value: PackageSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.manager = value.manager
  settingsForm.allowedGroups = [...value.allowedGroups]
  settingsForm.commandTimeoutSeconds = value.commandTimeoutSeconds
  settingsForm.jobTimeoutSeconds = value.jobTimeoutSeconds
  settingsForm.maxOutputMiB = bytesToMiB(value.maxOutputBytes)
  settingsForm.maxLogMiB = bytesToMiB(value.maxLogBytes)
  settingsForm.jobRetentionMinutes = value.jobRetentionMinutes
  settingsForm.maxPackagesPerJob = value.maxPackagesPerJob
  settingsForm.maxInstalledEntries = value.maxInstalledEntries
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getPackageSettings()
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
    const saved = await savePackageSettings({
      enabled: settingsForm.enabled,
      manager: settingsForm.manager,
      allowedGroups: settingsForm.allowedGroups
        .map((group) => group.trim())
        .filter((group) => group.length > 0),
      commandTimeoutSeconds: settingsForm.commandTimeoutSeconds,
      jobTimeoutSeconds: settingsForm.jobTimeoutSeconds,
      maxOutputBytes: Math.round(settingsForm.maxOutputMiB * 1024 * 1024),
      maxLogBytes: Math.round(settingsForm.maxLogMiB * 1024 * 1024),
      jobRetentionMinutes: settingsForm.jobRetentionMinutes,
      maxPackagesPerJob: settingsForm.maxPackagesPerJob,
      maxInstalledEntries: settingsForm.maxInstalledEntries,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    ElMessage.success('设置已保存并立即生效')
    await refreshAll()
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

async function loadInstalled(force = false): Promise<void> {
  if (installedLoaded.value && !force) {
    return
  }

  installedLoading.value = true
  try {
    const response = await listInstalled()
    installed.value = response.packages
    installedTotal.value = response.total
    installedTruncated.value = response.truncated
    installedLoaded.value = true
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载已安装列表失败')
  } finally {
    installedLoading.value = false
  }
}

async function loadUpdates(force = false): Promise<void> {
  if (updatesLoaded.value && !force) {
    return
  }

  updatesLoading.value = true
  try {
    const response = await listUpdates()
    updates.value = response.packages
    updatesLoaded.value = true
    markAvailable()
  } catch (error) {
    handleLoadError(error, '检查可用更新失败')
  } finally {
    updatesLoading.value = false
  }
}

async function loadSources(force = false): Promise<void> {
  if (sourcesLoaded.value && !force) {
    return
  }

  sourcesLoading.value = true
  try {
    const response = await listPackageSources()
    sources.value = response.sources
    sourcesSupported.value = response.supported
    sourcesReadOnly.value = response.readOnly
    sourcesLoaded.value = true
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载软件源失败')
  } finally {
    sourcesLoading.value = false
  }
}

async function loadActiveTab(force = false): Promise<void> {
  if (!status.value?.available) {
    return
  }

  switch (activeTab.value) {
    case 'installed':
      await loadInstalled(force)
      return
    case 'updates':
      await loadUpdates(force)
      return
    case 'sources':
      await loadSources(force)
      return
    default:
      // 安装页签依赖用户输入，不做自动加载。
      return
  }
}

async function refreshAll(): Promise<void> {
  refreshing.value = true
  try {
    await loadStatus()
    if (status.value?.available) {
      await loadActiveTab(true)
    }
  } finally {
    refreshing.value = false
    lastUpdated.value = new Date().toLocaleTimeString()
  }
}

async function runSearch(): Promise<void> {
  if (!status.value?.available) {
    return
  }

  const keyword = searchKeyword.value.trim()
  if (!keyword) {
    ElMessage.warning('请输入搜索关键词')
    return
  }

  searchLoading.value = true
  try {
    const response = await searchPackages(keyword)
    searchResults.value = response.results
    searchTruncated.value = response.truncated
    searchDone.value = true
    markAvailable()
  } catch (error) {
    handleLoadError(error, '搜索软件包失败')
  } finally {
    searchLoading.value = false
  }
}

// ---------------- 任务 ----------------

function stopJobPolling(): void {
  if (jobTimer !== undefined) {
    window.clearTimeout(jobTimer)
    jobTimer = undefined
  }
}

function appendJobLog(text: string): void {
  if (!text) {
    return
  }

  jobLog.value += text

  // 日志以 2 MiB 为服务端上限；前端再折叠一次，避免超长文本拖慢渲染。
  const maxDisplay = 200_000
  if (jobLog.value.length > maxDisplay) {
    jobLog.value = `…（较早日志已折叠）\n${jobLog.value.slice(-maxDisplay)}`
  }

  nextTick(() => {
    const element = logRef.value
    if (element && autoScroll.value) {
      element.scrollTop = element.scrollHeight
    }
  })
}

function onLogScroll(): void {
  const element = logRef.value
  if (!element) {
    return
  }

  autoScroll.value = element.scrollTop + element.clientHeight >= element.scrollHeight - 32
}

async function loadJobChunk(id: string): Promise<void> {
  try {
    const snapshot = await getPackageJob(id, jobOffset.value)
    appendJobLog(snapshot.log)
    jobOffset.value = snapshot.nextOffset
    job.value = snapshot

    if (snapshot.status !== 'running') {
      stopJobPolling()
      onJobFinished(snapshot)
      return
    }
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      stopJobPolling()
      if (job.value) {
        job.value = { ...job.value, status: 'failed' }
      }
      ElMessage.error(error.message)
      return
    }

    // 网络抖动时继续轮询，避免一次失败就丢失日志跟踪。
  }

  jobTimer = window.setTimeout(() => {
    void loadJobChunk(id)
  }, 1000)
}

function onJobFinished(snapshot: PackageJob): void {
  if (snapshot.status === 'succeeded') {
    ElMessage.success(`${jobTitle.value}完成`)
  } else if (snapshot.status === 'cancelled') {
    ElMessage.warning(`${jobTitle.value}已取消`)
  } else {
    ElMessage.error(`${jobTitle.value}失败，请查看执行日志`)
  }

  installedLoaded.value = false
  updatesLoaded.value = false
  sourcesLoaded.value = false
  void loadActiveTab(true)
}

async function startJob(action: PackageAction, packages: string[], title: string): Promise<void> {
  if (!canModify.value) {
    ElMessage.warning('Koi 服务未以 root 运行，无法执行包管理操作')
    return
  }

  jobStarting.value = true
  try {
    const started = await createPackageJob(action, packages)
    jobTitle.value = title
    jobOffset.value = 0
    jobLog.value = ''
    autoScroll.value = true
    job.value = {
      id: started.jobId,
      action: started.action,
      packages: started.packages,
      status: 'running',
      exitCode: null,
      log: '',
      nextOffset: 0,
      truncated: false,
      startedAt: new Date().toISOString(),
      endedAt: null,
    }
    jobDialogVisible.value = true
    markAvailable()
    stopJobPolling()
    void loadJobChunk(started.jobId)
  } catch (error) {
    showError(error)
  } finally {
    jobStarting.value = false
  }
}

async function confirmAndRun(
  action: PackageAction,
  packages: string[],
  title: string,
  message: string,
): Promise<void> {
  if (actionsDisabled.value) {
    if (!canModify.value) {
      ElMessage.warning('Koi 服务未以 root 运行，无法执行包管理操作')
    }
    return
  }

  try {
    await ElMessageBox.confirm(message, title, {
      type: 'warning',
      confirmButtonText: '执行',
      cancelButtonText: '取消',
    })
  } catch {
    return
  }

  await startJob(action, packages, title)
}

async function uninstall(item: PackageSummary): Promise<void> {
  await confirmAndRun(
    'remove',
    [item.name],
    `卸载 ${item.name}`,
    `确认卸载 ${item.name}？相关依赖可能被一并移除，正在运行的服务可能中断。`,
  )
}

async function installPackage(item: PackageSearchResult): Promise<void> {
  await confirmAndRun(
    'install',
    [item.name],
    `安装 ${item.name}`,
    `确认安装 ${item.name}？系统可能下载依赖并修改已安装软件。`,
  )
}

async function installManual(): Promise<void> {
  const name = manualName.value.trim()
  if (!name) {
    ElMessage.warning('请输入要安装的包名')
    return
  }

  await confirmAndRun(
    'install',
    [name],
    `安装 ${name}`,
    `确认安装 ${name}？系统可能下载依赖并修改已安装软件。`,
  )
}

async function upgradeOne(item: PackageUpdate): Promise<void> {
  await confirmAndRun(
    'upgrade',
    [item.name],
    `升级 ${item.name}`,
    `确认升级 ${item.name}？升级后通常需要重启相关服务。`,
  )
}

async function upgradeAll(): Promise<void> {
  if (updates.value.length === 0) {
    ElMessage.info('当前没有可用更新')
    return
  }

  await confirmAndRun(
    'upgrade-all',
    [],
    '系统更新',
    '确认执行系统更新？将升级全部可用更新，可能重启服务或要求重启主机。',
  )
}

async function refreshIndex(): Promise<void> {
  await startJob('refresh', [], '刷新包索引')
}

async function cancelRunningJob(): Promise<void> {
  if (!job.value || job.value.status !== 'running') {
    return
  }

  jobCancelling.value = true
  try {
    await cancelPackageJob(job.value.id)
    ElMessage.info('正在取消任务…')
  } catch (error) {
    showError(error)
  } finally {
    jobCancelling.value = false
  }
}

function handleJobDialogBeforeClose(done: () => void): void {
  void requestCloseJobDialog(done)
}

async function requestCloseJobDialog(done?: () => void): Promise<void> {
  if (job.value?.status === 'running') {
    try {
      await ElMessageBox.confirm(
        '任务仍在后台执行，关闭窗口后将不再显示实时日志；任务结束后操作按钮会自动恢复。',
        '关闭日志窗口',
        { type: 'warning', confirmButtonText: '关闭', cancelButtonText: '继续查看' },
      )
    } catch {
      return
    }
  }

  if (done) {
    done()
  } else {
    jobDialogVisible.value = false
  }
}

function jobTagType(value: PackageJobStatus | undefined): 'success' | 'danger' | 'warning' | 'info' {
  switch (value) {
    case 'succeeded':
      return 'success'
    case 'failed':
      return 'danger'
    case 'cancelled':
      return 'warning'
    default:
      return 'info'
  }
}

function jobStatusText(value: PackageJobStatus | undefined): string {
  switch (value) {
    case 'running':
      return '执行中'
    case 'succeeded':
      return '已完成'
    case 'failed':
      return '失败'
    case 'cancelled':
      return '已取消'
    default:
      return '未知'
  }
}

function actionText(value: PackageAction | undefined): string {
  switch (value) {
    case 'install':
      return '安装'
    case 'remove':
      return '卸载'
    case 'upgrade':
      return '升级'
    case 'upgrade-all':
      return '系统更新'
    case 'refresh':
      return '刷新包索引'
    default:
      return ''
  }
}

// ---------------- 软件源 ----------------

function openSourceDialog(): void {
  Object.assign(sourceForm, {
    name: '',
    uri: '',
    suite: '',
    components: '',
    gpgCheck: true,
  })
  sourceDialogVisible.value = true
}

async function submitSource(): Promise<void> {
  const name = sourceForm.name.trim()
  const uri = sourceForm.uri.trim()
  if (!name) {
    ElMessage.warning('请填写源标识')
    return
  }

  if (!uri) {
    ElMessage.warning('请填写仓库地址')
    return
  }

  if (isApt.value && (!sourceForm.suite.trim() || !sourceForm.components.trim())) {
    ElMessage.warning('APT 软件源必须填写发行版与组件')
    return
  }

  sourceSaving.value = true
  try {
    await addPackageSource({
      name,
      uri,
      suite: isApt.value ? sourceForm.suite.trim() : undefined,
      components: isApt.value ? sourceForm.components.trim() : undefined,
      gpgCheck: isRpm.value ? sourceForm.gpgCheck : undefined,
    })
    ElMessage.success('软件源已添加，建议刷新包索引')
    sourceDialogVisible.value = false
    await loadSources(true)
  } catch (error) {
    showError(error)
  } finally {
    sourceSaving.value = false
  }
}

async function toggleSource(item: PackageSource): Promise<void> {
  if (actionsDisabled.value) {
    return
  }

  busySourceId.value = item.id
  try {
    await setPackageSourceEnabled(item.id, !item.enabled)
    ElMessage.success(item.enabled ? '软件源已禁用' : '软件源已启用')
    await loadSources(true)
  } catch (error) {
    showError(error)
  } finally {
    busySourceId.value = ''
  }
}

async function deleteSource(item: PackageSource): Promise<void> {
  if (actionsDisabled.value) {
    return
  }

  try {
    await ElMessageBox.confirm(
      `确认删除软件源「${item.name}」？仅移除源配置，不会卸载已安装的软件。`,
      '删除软件源',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  busySourceId.value = item.id
  try {
    await removePackageSource(item.id)
    ElMessage.success('软件源已删除')
    await loadSources(true)
  } catch (error) {
    showError(error)
  } finally {
    busySourceId.value = ''
  }
}

function sourceSuiteText(item: PackageSource): string {
  const parts = [item.suite, item.components].filter((part) => part.length > 0)
  return parts.length > 0 ? parts.join(' / ') : '—'
}

watch(activeTab, () => {
  void loadActiveTab(false)
})

onMounted(async () => {
  await Promise.all([loadStatus(), loadSettingsAvailability()])
  await loadActiveTab(false)
})

onBeforeUnmount(() => {
  stopJobPolling()
})
</script>

<template>
  <div class="package-view">
    <div class="toolbar">
      <div class="toolbar-title">
        <div class="title-row">
          <span class="title">应用管理</span>
          <el-tag v-if="status" size="small" effect="plain">{{ managerLabel }}</el-tag>
          <el-tag v-if="status && !status.canModify" type="warning" size="small" effect="plain">
            只读
          </el-tag>
        </div>
        <span class="hint">
          通过系统包管理器安装、卸载与更新软件并管理软件源；变更操作以服务账号执行，等同于宿主 root
          权限，同一时间只执行一个任务。
        </span>
      </div>
      <div class="actions">
        <el-tooltip v-if="settingsAvailable" content="应用管理设置" placement="bottom">
          <el-button
            :icon="Setting"
            circle
            :loading="settingsLoading"
            aria-label="应用管理设置"
            @click="openSettings"
          />
        </el-tooltip>
        <span v-if="lastUpdated" class="updated">更新于 {{ lastUpdated }}</span>
        <el-button
          :icon="Download"
          :disabled="actionsDisabled"
          :loading="jobStarting"
          @click="refreshIndex"
        >
          刷新索引
        </el-button>
        <el-button :icon="Refresh" :loading="refreshing" @click="refreshAll">刷新</el-button>
      </div>
    </div>

    <el-alert
      v-if="forbidden"
      class="notice"
      type="error"
      show-icon
      :closable="false"
      title="当前账号无权访问应用管理"
      :description="forbiddenDetail || '请将账号加入允许的管理员组（sudo / wheel）后重新登录。'"
    />
    <el-alert
      v-else-if="unavailable"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="包管理器不可用"
      :description="errorMessage || '请确认系统已安装 apt/dnf/yum/pacman/zypper 之一。'"
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
    <el-alert
      v-if="status && !status.canModify"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="Koi 服务未以 root 运行"
      description="当前仅能查看软件包，安装、卸载、更新与软件源管理不可用。请以 root 运行 koi.service 后重试。"
    />

    <el-tabs v-if="status" v-model="activeTab" class="package-tabs">
      <el-tab-pane label="已安装" name="installed">
        <div class="pane">
          <div class="pane-toolbar">
            <el-input
              v-model="installedFilter"
              class="filter-input"
              placeholder="按名称过滤已安装应用"
              clearable
            />
            <span class="hint">
              共 {{ filteredInstalled.length }} / {{ installed.length }} 个已安装应用（总计
              {{ installedTotal }}）
              <template v-if="installedTruncated">，列表已截断</template>
            </span>
          </div>
          <div class="table-wrap">
            <el-table
              v-loading="installedLoading"
              :data="filteredInstalled"
              height="100%"
              border
              stripe
              empty-text="没有匹配的已安装应用"
            >
              <el-table-column prop="name" label="名称" min-width="240" show-overflow-tooltip />
              <el-table-column prop="version" label="版本" width="240" show-overflow-tooltip />
              <el-table-column label="操作" width="120" fixed="right" align="center">
                <template #default="{ row }">
                  <el-button
                    size="small"
                    type="danger"
                    plain
                    :disabled="actionsDisabled"
                    @click="uninstall(row)"
                  >
                    卸载
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane label="安装" name="install">
        <div class="pane">
          <div class="pane-toolbar">
            <el-input
              v-model="searchKeyword"
              class="search-input"
              placeholder="搜索可安装的软件包，例如 nginx"
              clearable
              @keyup.enter="runSearch"
            >
              <template #append>
                <el-button :icon="Search" :loading="searchLoading" @click="runSearch">搜索</el-button>
              </template>
            </el-input>
            <span v-if="searchDone" class="hint">
              找到 {{ searchResults.length }} 个结果
              <template v-if="searchTruncated">（仅显示前 200 条）</template>
            </span>
          </div>
          <div class="pane-toolbar">
            <el-input
              v-model="manualName"
              class="filter-input"
              placeholder="直接输入精确包名，例如 nginx"
              clearable
              @keyup.enter="installManual"
            />
            <el-button
              type="primary"
              :icon="Download"
              :disabled="actionsDisabled"
              @click="installManual"
            >
              安装指定包
            </el-button>
            <span class="hint">搜索使用本地包索引；安装时会自动解析并下载依赖。</span>
          </div>
          <div class="table-wrap">
            <el-table
              v-loading="searchLoading"
              :data="searchResults"
              height="100%"
              border
              stripe
              empty-text="输入关键词后搜索可安装的软件包"
            >
              <el-table-column prop="name" label="名称" min-width="220" show-overflow-tooltip />
              <el-table-column label="版本" width="180" show-overflow-tooltip>
                <template #default="{ row }">{{ row.version || '—' }}</template>
              </el-table-column>
              <el-table-column prop="summary" label="简介" min-width="280" show-overflow-tooltip />
              <el-table-column label="状态" width="100" align="center">
                <template #default="{ row }">
                  <el-tag v-if="row.installed" size="small" type="success" effect="plain">
                    已安装
                  </el-tag>
                  <span v-else>—</span>
                </template>
              </el-table-column>
              <el-table-column label="操作" width="110" fixed="right" align="center">
                <template #default="{ row }">
                  <el-button
                    size="small"
                    type="primary"
                    plain
                    :disabled="actionsDisabled"
                    @click="installPackage(row)"
                  >
                    安装
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane :label="`更新（${updates.length}）`" name="updates">
        <div class="pane">
          <div class="pane-toolbar">
            <el-button
              type="primary"
              :icon="Upload"
              :disabled="actionsDisabled || updates.length === 0"
              @click="upgradeAll"
            >
              系统更新
            </el-button>
            <el-button :icon="Refresh" :loading="updatesLoading" @click="loadUpdates(true)">
              重新检查
            </el-button>
            <span class="hint">共 {{ updates.length }} 个可用更新</span>
          </div>
          <div class="table-wrap">
            <el-table
              v-loading="updatesLoading"
              :data="updates"
              height="100%"
              border
              stripe
              empty-text="当前没有可用更新"
            >
              <el-table-column prop="name" label="名称" min-width="240" show-overflow-tooltip />
              <el-table-column label="当前版本" width="220" show-overflow-tooltip>
                <template #default="{ row }">{{ row.currentVersion || '—' }}</template>
              </el-table-column>
              <el-table-column prop="newVersion" label="新版本" width="220" show-overflow-tooltip />
              <el-table-column label="操作" width="110" fixed="right" align="center">
                <template #default="{ row }">
                  <el-button
                    size="small"
                    type="primary"
                    plain
                    :disabled="actionsDisabled"
                    @click="upgradeOne(row)"
                  >
                    升级
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane label="软件源" name="sources">
        <div class="pane">
          <div class="pane-toolbar">
            <el-button
              type="primary"
              :icon="Plus"
              :disabled="actionsDisabled || !sourcesSupported"
              @click="openSourceDialog"
            >
              添加软件源
            </el-button>
            <el-button :icon="Refresh" :loading="sourcesLoading" @click="loadSources(true)">
              刷新
            </el-button>
            <span v-if="sourcesReadOnly" class="hint">
              当前包管理器的软件源仅支持查看（需手动编辑配置文件）
            </span>
            <span v-else class="hint">修改软件源后建议刷新包索引</span>
          </div>
          <div class="table-wrap">
            <el-table
              v-loading="sourcesLoading"
              :data="sources"
              height="100%"
              border
              stripe
              empty-text="没有软件源"
            >
              <el-table-column label="名称" min-width="200" show-overflow-tooltip>
                <template #default="{ row }">
                  {{ row.name }}
                  <el-tag v-if="row.managed" size="small" type="success" effect="plain">
                    由 Koi 创建
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="uri" label="地址" min-width="260" show-overflow-tooltip>
                <template #default="{ row }">{{ row.uri || '—' }}</template>
              </el-table-column>
              <el-table-column v-if="isApt" label="发行版 / 组件" min-width="180">
                <template #default="{ row }">{{ sourceSuiteText(row) }}</template>
              </el-table-column>
              <el-table-column label="状态" width="100" align="center">
                <template #default="{ row }">
                  <el-tag :type="row.enabled ? 'success' : 'info'" size="small" effect="plain">
                    {{ row.enabled ? '已启用' : '已禁用' }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="file" label="来源" min-width="220" show-overflow-tooltip />
              <el-table-column label="操作" width="170" fixed="right" align="center">
                <template #default="{ row }">
                  <el-button
                    size="small"
                    :loading="busySourceId === row.id"
                    :disabled="actionsDisabled || !row.togglable"
                    @click="toggleSource(row)"
                  >
                    {{ row.enabled ? '禁用' : '启用' }}
                  </el-button>
                  <el-button
                    size="small"
                    type="danger"
                    plain
                    :disabled="actionsDisabled || !row.removable"
                    @click="deleteSource(row)"
                  >
                    删除
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>

    <el-empty
      v-else-if="!statusLoading"
      description="无法加载包管理器状态，请检查权限或系统环境"
    />

    <el-dialog v-model="sourceDialogVisible" title="添加软件源" width="540px">
      <el-form label-width="90px">
        <el-form-item label="源标识" required>
          <el-input v-model="sourceForm.name" placeholder="例如 koi-extra" />
          <div class="form-hint">用于生成配置文件名或 zypper 别名，只能包含字母、数字、_ . -。</div>
        </el-form-item>
        <el-form-item label="仓库地址" required>
          <el-input v-model="sourceForm.uri" placeholder="例如 https://mirrors.example.com/debian" />
        </el-form-item>
        <el-form-item v-if="isApt" label="发行版" required>
          <el-input v-model="sourceForm.suite" placeholder="例如 bookworm" />
        </el-form-item>
        <el-form-item v-if="isApt" label="组件" required>
          <el-input v-model="sourceForm.components" placeholder="例如 main contrib non-free" />
        </el-form-item>
        <el-form-item v-if="isRpm" label="校验签名">
          <el-switch v-model="sourceForm.gpgCheck" />
          <div class="form-hint">关闭后不校验仓库元数据签名，仅建议用于受信任的内网源。</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="sourceDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="sourceSaving" @click="submitSource">添加</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="jobDialogVisible"
      :title="jobDialogTitle"
      width="760px"
      top="5vh"
      :close-on-click-modal="false"
      :before-close="handleJobDialogBeforeClose"
    >
      <div class="job-toolbar">
        <el-tag :type="jobTagType(job?.status)" size="small" effect="plain">
          {{ jobStatusText(job?.status) }}
        </el-tag>
        <span class="hint">
          {{ actionText(job?.action) }}
          <template v-if="job && job.packages.length > 0">：{{ job.packages.join('、') }}</template>
        </span>
        <span v-if="job?.exitCode !== null && job?.exitCode !== undefined" class="hint">
          退出码 {{ job.exitCode }}
        </span>
        <span v-if="job?.truncated" class="hint">日志已截断</span>
      </div>
      <pre ref="logRef" class="job-log" @scroll="onLogScroll">{{ jobLog || '等待输出…' }}</pre>
      <template #footer>
        <el-button
          v-if="job?.status === 'running'"
          type="danger"
          plain
          :loading="jobCancelling"
          @click="cancelRunningJob"
        >
          取消任务
        </el-button>
        <el-button @click="requestCloseJobDialog()">关闭</el-button>
      </template>
    </el-dialog>

    <el-drawer
      v-model="settingsVisible"
      title="应用管理设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 packages），保存后立即生效，无需重启服务。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用应用管理">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后所有账号（含 root）都无法使用应用管理接口。</div>
        </el-form-item>
        <el-form-item label="包管理器">
          <el-select v-model="settingsForm.manager" class="full-width">
            <el-option
              v-for="option in managerOptions"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
          <div class="form-hint">自动检测按 apt → dnf → yum → pacman → zypper 顺序探测。</div>
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

        <el-divider content-position="left">超时</el-divider>
        <el-form-item label="查询超时（秒）">
          <el-input-number
            v-model="settingsForm.commandTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="86400"
            :step="10"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="任务超时（秒）">
          <el-input-number
            v-model="settingsForm.jobTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="172800"
            :step="60"
            controls-position="right"
          />
        </el-form-item>

        <el-divider content-position="left">上限</el-divider>
        <el-form-item label="查询输出上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxOutputMiB"
            class="full-width"
            :min="1"
            :max="256"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="任务日志上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxLogMiB"
            class="full-width"
            :min="1"
            :max="256"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="任务保留时间（分钟）">
          <el-input-number
            v-model="settingsForm.jobRetentionMinutes"
            class="full-width"
            :min="1"
            :max="10080"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="单个任务的软件包上限">
          <el-input-number
            v-model="settingsForm.maxPackagesPerJob"
            class="full-width"
            :min="1"
            :max="1000"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="已安装列表条目上限">
          <el-input-number
            v-model="settingsForm.maxInstalledEntries"
            class="full-width"
            :min="1"
            :max="1000000"
            controls-position="right"
          />
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
.package-view {
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

.title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title-row :deep(.el-tag) {
  max-width: 420px;
  overflow: hidden;
  text-overflow: ellipsis;
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

.package-tabs {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
}

.package-tabs :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: 8px;
}

.package-tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
}

.package-tabs :deep(.el-tab-pane) {
  height: 100%;
}

.pane {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.pane-toolbar {
  display: flex;
  flex: none;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.filter-input {
  width: 320px;
}

.search-input {
  width: 460px;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}

.job-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.job-log {
  box-sizing: border-box;
  height: 58vh;
  margin: 0;
  padding: 12px;
  overflow: auto;
  background: #1f2430;
  border-radius: 6px;
  color: #e6e6e6;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 12px;
  line-height: 1.6;
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
</style>
