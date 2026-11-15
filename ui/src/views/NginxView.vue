<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CircleCheck, Refresh, RefreshRight, Setting } from '@element-plus/icons-vue'
import {
  getNginxSettings,
  getNginxStatus,
  listNginxConfigs,
  reloadNginxConfig,
  saveNginxSettings,
  testNginxConfig,
  type NginxConfigEntry,
  type NginxSettings,
  type NginxStatus,
} from '@/api/nginx'
import { ApiError } from '@/api/http'
import { useEditorStore } from '@/stores/editor'
import { useWorkspaceStore } from '@/stores/workspace'

const editor = useEditorStore()
const workspace = useWorkspaceStore()

const status = ref<NginxStatus | null>(null)
const files = ref<NginxConfigEntry[]>([])
const directory = ref('')
const truncated = ref(false)
const loading = ref(false)
const lastUpdated = ref('')

const forbidden = ref(false)
const forbiddenDetail = ref('')
const unavailable = ref(false)
const errorMessage = ref('')

const testLoading = ref(false)
const reloadLoading = ref(false)
const commandVisible = ref(false)
const commandTitle = ref('')
const commandSuccess = ref(false)
const commandOutput = ref('')

// ---------------- Nginx 管理设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<NginxSettings | null>(null)
const settingsForm = reactive({
  enabled: true,
  nginxPath: '',
  confDirectory: '/etc/nginx/conf.d',
  mainConfigPath: '',
  allowedGroups: [] as string[],
  commandTimeoutSeconds: 30,
  maxOutputMiB: 1,
  maxFileMiB: 1,
  maxListEntries: 1000,
})
const groupSuggestions = ['sudo', 'wheel']

const configCountText = computed(() => {
  const suffix = truncated.value ? '（已截断）' : ''
  return `共 ${files.value.length} 个配置文件${suffix}`
})

/** 加载失败提示：403（非允许组）与 503（nginx 不可用/功能关闭）单独展示。 */
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

/** 任一请求成功即说明权限与 nginx 可用性正常，清除持久提示。 */
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

function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KiB`
  }

  return `${(bytes / (1024 * 1024)).toFixed(2)} MiB`
}

function formatTime(timestamp: number): string {
  if (!timestamp) {
    return '—'
  }

  return new Date(timestamp).toLocaleString()
}

function nowText(): string {
  return new Date().toLocaleTimeString()
}

/** 刷新状态与文件清单；自动刷新失败时保留上一次清单。 */
async function refresh(): Promise<void> {
  loading.value = true
  try {
    const [statusValue, listValue] = await Promise.all([getNginxStatus(), listNginxConfigs()])
    status.value = statusValue
    directory.value = listValue.directory
    files.value = listValue.files
    truncated.value = listValue.truncated
    lastUpdated.value = nowText()
    markAvailable()
  } catch (error) {
    handleLoadError(error, '加载 Nginx 配置失败')
  } finally {
    loading.value = false
  }
}

/** 点击文件：复用「文本编辑」工作区页签，保存经 Nginx 管理 API 完成。 */
async function openConfig(row: NginxConfigEntry): Promise<void> {
  const path = directory.value ? `${directory.value.replace(/\/+$/, '')}/${row.name}` : row.name
  workspace.open('editor')
  await editor.openNginxConfig(row.name, path)
}

function showCommandResult(title: string, success: boolean, output: string): void {
  commandTitle.value = title
  commandSuccess.value = success
  commandOutput.value = output
  commandVisible.value = true
}

async function runTest(): Promise<void> {
  testLoading.value = true
  try {
    const result = await testNginxConfig()
    markAvailable()
    showCommandResult('Nginx 配置检测', result.success, result.output)
    if (result.success) {
      ElMessage.success('nginx -t 检测通过')
    } else {
      ElMessage.warning('nginx -t 检测未通过，请查看输出')
    }
  } catch (error) {
    handleLoadError(error, '执行 nginx -t 失败')
    showError(error)
  } finally {
    testLoading.value = false
  }
}

async function runReload(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      '热加载会通知 nginx 主进程重新读取配置。建议先执行【检测配置】，确认语法无误后再热加载。确定继续吗？',
      '热加载 Nginx 配置',
      {
        type: 'warning',
        confirmButtonText: '热加载',
        cancelButtonText: '取消',
      },
    )
  } catch {
    return
  }

  reloadLoading.value = true
  try {
    const result = await reloadNginxConfig()
    markAvailable()
    showCommandResult('Nginx 热加载', result.success, result.output)
    if (result.success) {
      ElMessage.success('配置已热加载')
    } else {
      ElMessage.error('nginx -s reload 未成功，请查看输出')
    }
  } catch (error) {
    handleLoadError(error, '执行 nginx -s reload 失败')
    showError(error)
  } finally {
    reloadLoading.value = false
  }
}

// ---------------- 设置 ----------------

function bytesToMiB(bytes: number): number {
  return Math.max(1, Math.round(bytes / (1024 * 1024)))
}

function applySettingsForm(value: NginxSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.nginxPath = value.nginxPath
  settingsForm.confDirectory = value.confDirectory
  settingsForm.mainConfigPath = value.mainConfigPath
  settingsForm.allowedGroups = [...value.allowedGroups]
  settingsForm.commandTimeoutSeconds = value.commandTimeoutSeconds
  settingsForm.maxOutputMiB = bytesToMiB(value.maxOutputBytes)
  settingsForm.maxFileMiB = bytesToMiB(value.maxFileBytes)
  settingsForm.maxListEntries = value.maxListEntries
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getNginxSettings()
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
    const saved = await saveNginxSettings({
      enabled: settingsForm.enabled,
      nginxPath: settingsForm.nginxPath.trim(),
      confDirectory: settingsForm.confDirectory.trim(),
      mainConfigPath: settingsForm.mainConfigPath.trim(),
      allowedGroups: settingsForm.allowedGroups
        .map((group) => group.trim())
        .filter((group) => group.length > 0),
      commandTimeoutSeconds: settingsForm.commandTimeoutSeconds,
      maxOutputBytes: Math.round(settingsForm.maxOutputMiB * 1024 * 1024),
      maxFileBytes: Math.round(settingsForm.maxFileMiB * 1024 * 1024),
      maxListEntries: settingsForm.maxListEntries,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    ElMessage.success('设置已保存并立即生效')
    await refresh()
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

onMounted(() => {
  void loadSettingsAvailability()
  void refresh()
})
</script>

<template>
  <div class="nginx-view">
    <div class="toolbar">
      <div class="toolbar-title">
        <span class="title">Nginx 管理</span>
        <span class="hint">
          查看 conf.d 配置清单并复用「文本编辑」在线编辑；检测与热加载以服务账号执行，等同于宿主 root
          权限，数据仅在手动刷新时更新。
        </span>
      </div>
      <div class="actions">
        <el-tag v-if="status" type="success" effect="plain">
          {{ status.version || 'nginx 已就绪' }}
        </el-tag>
        <el-button :icon="CircleCheck" :loading="testLoading" @click="runTest">检测配置</el-button>
        <el-button :icon="RefreshRight" :loading="reloadLoading" @click="runReload">
          热加载
        </el-button>
        <el-tooltip v-if="settingsAvailable" content="Nginx 管理设置" placement="bottom">
          <el-button
            :icon="Setting"
            circle
            :loading="settingsLoading"
            aria-label="Nginx 管理设置"
            @click="openSettings"
          />
        </el-tooltip>
        <el-button :icon="Refresh" :loading="loading" @click="refresh">刷新</el-button>
      </div>
    </div>

    <el-alert
      v-if="forbidden"
      class="notice"
      type="error"
      show-icon
      :closable="false"
      title="当前账号无权访问 Nginx 管理"
      :description="forbiddenDetail || '请将账号加入允许的管理员组（sudo / wheel）后重新登录。'"
    />
    <el-alert
      v-else-if="unavailable"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="Nginx 管理不可用"
      :description="errorMessage || '请确认已安装 nginx，并检查 Nginx 管理设置。'"
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
      <span class="hint">目录：{{ directory || '—' }}</span>
      <span class="hint">{{ configCountText }}</span>
      <span v-if="lastUpdated" class="hint">更新于 {{ lastUpdated }}</span>
    </div>

    <div class="table-wrap">
      <el-table
        v-loading="loading"
        :data="files"
        height="100%"
        border
        stripe
        empty-text="conf.d 目录中没有可编辑的 .conf 文件"
      >
        <el-table-column label="配置文件" min-width="280">
          <template #default="{ row }">
            <span class="config-name" @click="openConfig(row)">{{ row.name }}</span>
          </template>
        </el-table-column>
        <el-table-column label="大小" width="120">
          <template #default="{ row }">{{ formatSize(row.size) }}</template>
        </el-table-column>
        <el-table-column label="修改时间" width="190">
          <template #default="{ row }">{{ formatTime(row.modifiedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="100" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openConfig(row)">编辑</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="commandVisible" :title="commandTitle" width="720px">
      <el-alert
        :type="commandSuccess ? 'success' : 'error'"
        :closable="false"
        show-icon
        :title="commandSuccess ? '命令执行成功' : '命令执行失败'"
      />
      <pre class="command-output">{{ commandOutput || '（无输出）' }}</pre>
      <template #footer>
        <el-button @click="commandVisible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-drawer
      v-model="settingsVisible"
      title="Nginx 管理设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 nginx），保存后立即生效，无需重启服务。
        配置文件由服务进程以服务账号（通常为 root）直接读写，等同于宿主管理员能力，请谨慎设置允许组。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用 Nginx 管理">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后所有账号（含 root）都无法使用 Nginx 管理接口。</div>
        </el-form-item>
        <el-form-item label="nginx 路径">
          <el-input
            v-model="settingsForm.nginxPath"
            placeholder="留空自动探测 /usr/sbin/nginx、/usr/local/nginx/sbin/nginx 与 PATH"
          />
          <div class="form-hint">非空时必须是绝对路径；路径不存在会使 Nginx 管理返回 503。</div>
        </el-form-item>
        <el-form-item label="conf.d 目录">
          <el-input v-model="settingsForm.confDirectory" placeholder="/etc/nginx/conf.d" />
          <div class="form-hint">仅该目录顶层的 *.conf 普通文件会被列出和允许编辑；不支持符号链接。</div>
        </el-form-item>
        <el-form-item label="主配置路径">
          <el-input
            v-model="settingsForm.mainConfigPath"
            placeholder="留空使用 nginx 编译期默认配置"
          />
          <div class="form-hint">
            非空时 nginx -t 与 nginx -s reload 都会追加 -c 参数；适用于源码编译安装。
          </div>
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

        <el-divider content-position="left">超时与上限</el-divider>
        <el-form-item label="命令超时（秒）">
          <el-input-number
            v-model="settingsForm.commandTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="命令输出上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxOutputMiB"
            class="full-width"
            :min="1"
            :max="16"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="单个配置文件上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxFileMiB"
            class="full-width"
            :min="1"
            :max="16"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="文件清单条目上限">
          <el-input-number
            v-model="settingsForm.maxListEntries"
            class="full-width"
            :min="1"
            :max="10000"
            :step="100"
            controls-position="right"
          />
          <div class="form-hint">超出时按名称排序截断，并在清单提示中标记。</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="settingsSaving" @click="settingsVisible = false">取消</el-button>
        <el-button type="primary" :loading="settingsSaving" @click="saveSettings">保存</el-button>
      </template>
    </el-drawer>
  </div>
</template>

<style scoped>
.nginx-view {
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

.notice {
  flex: none;
  margin-bottom: 12px;
}

.pane-toolbar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 16px;
  margin-bottom: 8px;
}

.table-wrap {
  flex: 1;
  min-height: 0;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
}

.config-name {
  color: #409eff;
  cursor: pointer;
}

.config-name:hover {
  text-decoration: underline;
}

.command-output {
  box-sizing: border-box;
  max-height: 52vh;
  margin: 12px 0 0;
  padding: 12px;
  overflow: auto;
  color: #d8dee9;
  font-family: 'Menlo', 'Consolas', 'Liberation Mono', monospace;
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
  background: #1e1e1e;
  border-radius: 6px;
}

.settings-tip {
  margin: 0 0 16px;
  color: #606266;
  font-size: 12px;
  line-height: 1.6;
}

.settings-form :deep(.el-divider) {
  margin: 8px 0 16px;
}

.settings-form :deep(.el-divider__text) {
  color: #909399;
  font-size: 12px;
}

.full-width {
  width: 100%;
}

.form-hint {
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}
</style>
