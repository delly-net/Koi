<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Setting } from '@element-plus/icons-vue'
import TerminalPane from '@/components/terminal/TerminalPane.vue'
import { ApiError } from '@/api/http'
import {
  getTerminalSettings,
  saveTerminalSettings,
  type TerminalSettings,
} from '@/api/terminal'
import { useTerminalStore } from '@/stores/terminal'

interface TerminalTab {
  id: string
  title: string
  /** 初始工作目录；空串表示账号家目录。 */
  cwd: string
  /** 终端就绪后自动执行一次的命令；空串表示普通交互式 Shell。 */
  command: string
}

const tabs = ref<TerminalTab[]>([])
const activeKey = ref('')

let counter = 0

/** 页签标题中的目录名：根目录显示 /，其余取最后一段。 */
function directoryLabel(path: string): string {
  if (!path || path === '/') {
    return '/'
  }

  const parts = path.split('/').filter(Boolean)
  return parts[parts.length - 1] ?? '/'
}

/**
 * 新建一个终端标签；每个标签对应一条独立的 WebSocket 与 PTY。
 * cwd 来自文件管理「打开终端」，标题里同时标出目录名；
 * command 来自文件管理「执行」，终端就绪后自动执行一次。
 */
function createTab(cwd = '', command = ''): void {
  counter += 1
  const id = `terminal-${counter}-${Date.now()}`
  const title = cwd ? `终端 ${counter} · ${directoryLabel(cwd)}` : `终端 ${counter}`
  tabs.value.push({ id, title, cwd, command })
  activeKey.value = id
}

/** 关闭标签：销毁对应终端面板（WS 关闭、PTY 回收），至少保留一个。 */
function removeTab(name: string | number): void {
  const key = String(name)
  const index = tabs.value.findIndex((tab) => tab.id === key)
  if (index === -1) {
    return
  }

  const wasActive = activeKey.value === key
  tabs.value.splice(index, 1)

  if (wasActive) {
    const next = tabs.value[index] ?? tabs.value[index - 1] ?? null
    activeKey.value = next?.id ?? ''
  }

  if (tabs.value.length === 0) {
    createTab()
  }
}

const terminal = useTerminalStore()

// 终端标签可能在请求发出时尚未挂载（工作区按需挂载），setup 时先消费未处理的请求。
const initialRequest = terminal.consume()
createTab(initialRequest?.cwd ?? '', initialRequest?.command ?? '')

// 终端标签已打开时，文件管理等模块再次请求新页签走这里；消费后清空避免重复创建。
watch(
  () => terminal.request,
  (request) => {
    if (!request) {
      return
    }

    const consumed = terminal.consume()
    if (consumed) {
      createTab(consumed.cwd, consumed.command)
    }
  },
)

// ---------------- 终端设置（仅 root） ----------------

/** 启动参数输入建议。 */
const argumentSuggestions = ['-l', '-i']

const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<TerminalSettings | null>(null)
const settingsForm = reactive({
  enabled: true,
  shell: '',
  shellArguments: [] as string[],
  maxSessionsPerUser: 4,
  maxSessionsTotal: 16,
  maxInputKiB: 64,
  defaultCols: 120,
  defaultRows: 32,
  closeTimeoutSeconds: 5,
})

function bytesToKiB(bytes: number): number {
  return Math.max(1, Math.round(bytes / 1024))
}

function applySettingsForm(value: TerminalSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.shell = value.shell
  settingsForm.shellArguments = [...value.shellArguments]
  settingsForm.maxSessionsPerUser = value.maxSessionsPerUser
  settingsForm.maxSessionsTotal = value.maxSessionsTotal
  settingsForm.maxInputKiB = bytesToKiB(value.maxInputBytes)
  settingsForm.defaultCols = value.defaultCols
  settingsForm.defaultRows = value.defaultRows
  settingsForm.closeTimeoutSeconds = value.closeTimeoutSeconds
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getTerminalSettings()
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

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

async function saveSettings(): Promise<void> {
  settingsSaving.value = true
  try {
    const saved = await saveTerminalSettings({
      enabled: settingsForm.enabled,
      shell: settingsForm.shell.trim(),
      shellArguments: settingsForm.shellArguments
        .map((argument) => argument.trim())
        .filter((argument) => argument.length > 0),
      maxSessionsPerUser: settingsForm.maxSessionsPerUser,
      maxSessionsTotal: settingsForm.maxSessionsTotal,
      maxInputBytes: Math.round(settingsForm.maxInputKiB * 1024),
      defaultCols: settingsForm.defaultCols,
      defaultRows: settingsForm.defaultRows,
      closeTimeoutSeconds: settingsForm.closeTimeoutSeconds,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    ElMessage.success('设置已保存并立即生效')
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

onMounted(() => {
  void loadSettingsAvailability()
})
</script>

<template>
  <div class="terminal-view">
    <div class="toolbar">
      <span class="title">在线终端</span>
      <span class="hint">每个标签为独立会话；断开或刷新后需重新连接</span>
      <el-tooltip v-if="settingsAvailable" content="终端设置" placement="bottom">
        <el-button
          class="settings-button"
          :icon="Setting"
          circle
          :loading="settingsLoading"
          aria-label="终端设置"
          @click="openSettings"
        />
      </el-tooltip>
    </div>

    <el-tabs
      v-model="activeKey"
      type="card"
      editable
      class="tabs"
      @tab-add="createTab"
      @tab-remove="removeTab"
    >
      <el-tab-pane
        v-for="tab in tabs"
        :key="tab.id"
        :label="tab.title"
        :name="tab.id"
        :closable="tabs.length > 1"
      >
        <TerminalPane
          :active="activeKey === tab.id"
          :cwd="tab.cwd"
          :initial-command="tab.command"
        />
      </el-tab-pane>
    </el-tabs>

    <el-drawer
      v-model="settingsVisible"
      title="终端设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 terminal），保存后立即生效，无需重启服务；已打开的终端不受影响。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用在线终端">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后新的终端连接返回 503，已打开的会话不受影响。</div>
        </el-form-item>
        <el-form-item label="Shell 路径">
          <el-input v-model="settingsForm.shell" placeholder="/bin/bash（留空使用系统账号的登录 Shell）" />
          <div class="form-hint">必须是绝对路径；留空表示使用登录账号在系统账号库中的登录 Shell。</div>
        </el-form-item>
        <el-form-item label="Shell 启动参数">
          <el-select
            v-model="settingsForm.shellArguments"
            class="full-width"
            multiple
            filterable
            allow-create
            default-first-option
            placeholder="输入参数后按回车添加"
          >
            <el-option
              v-for="argument in argumentSuggestions"
              :key="argument"
              :label="argument"
              :value="argument"
            />
          </el-select>
          <div class="form-hint">每个标签代表一个独立参数，例如 -l；最多 16 项。</div>
        </el-form-item>

        <el-divider content-position="left">会话</el-divider>
        <el-form-item label="每账号并发终端数">
          <el-input-number
            v-model="settingsForm.maxSessionsPerUser"
            class="full-width"
            :min="1"
            :max="128"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="终端会话总数上限">
          <el-input-number
            v-model="settingsForm.maxSessionsTotal"
            class="full-width"
            :min="1"
            :max="1024"
            controls-position="right"
          />
        </el-form-item>

        <el-divider content-position="left">显示</el-divider>
        <el-form-item label="默认列数">
          <el-input-number
            v-model="settingsForm.defaultCols"
            class="full-width"
            :min="1"
            :max="1000"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="默认行数">
          <el-input-number
            v-model="settingsForm.defaultRows"
            class="full-width"
            :min="1"
            :max="1000"
            controls-position="right"
          />
        </el-form-item>

        <el-divider content-position="left">限制</el-divider>
        <el-form-item label="单条输入上限（KiB）">
          <el-input-number
            v-model="settingsForm.maxInputKiB"
            class="full-width"
            :min="1"
            :max="4096"
            controls-position="right"
          />
          <div class="form-hint">单条客户端消息超过该值时终端会被关闭，默认 64 KiB。</div>
        </el-form-item>
        <el-form-item label="关闭会话超时（秒）">
          <el-input-number
            v-model="settingsForm.closeTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="120"
            controls-position="right"
          />
          <div class="form-hint">等待子进程退出的时间，超时后强制终止。</div>
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
.terminal-view {
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

.hint {
  margin-left: auto;
  color: #909399;
  font-size: 12px;
}

.settings-button {
  flex: none;
  margin-left: 2px;
}

.tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.tabs :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: 8px;
}

.tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.tabs :deep(.el-tab-pane) {
  height: 100%;
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
  margin: 8px 0 16px;
}

.form-hint {
  width: 100%;
  margin-top: 4px;
  color: #8a919f;
  font-size: 12px;
  line-height: 1.5;
}

.full-width {
  width: 100%;
}
</style>
