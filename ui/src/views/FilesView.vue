<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Setting } from '@element-plus/icons-vue'
import {
  getFileRoots,
  getFileSettings,
  saveFileSettings,
  type FileSettings,
} from '@/api/files'
import { ApiError } from '@/api/http'
import FileBrowserPane from '@/components/files/FileBrowserPane.vue'

interface FileTab {
  id: string
  /** 页签标题随当前目录变化。 */
  title: string
  /** 页签创建时的目录，挂载后由面板自行导航。 */
  initialPath: string
}

const tabs = ref<FileTab[]>([])
const activeKey = ref('')
const homePath = ref('')
const loadingRoots = ref(false)
/** 设置中关闭文件管理后显示的常驻提示。 */
const disabledNotice = ref(false)
let counter = 0

// ---------------- 文件管理设置（仅 root） ----------------
const settingsAvailable = ref(false)
const settingsVisible = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsSnapshot = ref<FileSettings | null>(null)
const rootSuggestions = ['/', '/home', '/srv', '/opt', '/var/log']
const settingsForm = reactive({
  enabled: true,
  allowedRoots: [] as string[],
  maxUploadMiB: 100,
  maxListEntries: 5000,
  maxTextMiB: 5,
  helperTimeoutSeconds: 120,
  listTimeoutSeconds: 300,
  transferTimeoutSeconds: 1800,
  streamIdleTimeoutSeconds: 120,
  streamMaxSeconds: 0,
})

/** 页签标题取目录名，根目录显示 /；重名目录允许出现同名页签。 */
function titleForPath(path: string): string {
  if (!path || path === '/') {
    return '/'
  }

  const parts = path.split('/').filter(Boolean)
  return parts[parts.length - 1] ?? '/'
}

/** 新建页签并激活；path 可来自家目录或目录行菜单【在新页签打开】。 */
function addTab(path: string): void {
  if (!path) {
    ElMessage.warning('尚未获取家目录，请稍后重试')
    return
  }

  counter += 1
  const id = `files-${counter}-${Date.now()}`
  tabs.value.push({ id, title: titleForPath(path), initialPath: path })
  activeKey.value = id
}

function updateTabTitle(id: string, path: string): void {
  const tab = tabs.value.find((item) => item.id === id)
  if (tab) {
    tab.title = titleForPath(path)
  }
}

/** 关闭页签：至少保留一个，关闭最后一个后回到家目录。 */
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
    addTab(homePath.value)
  }
}

function showError(error: unknown): void {
  if (error instanceof ApiError) {
    ElMessage.error(error.detail ? `${error.message}：${error.detail}` : error.message)
    return
  }

  ElMessage.error(error instanceof Error ? error.message : '操作失败')
}

function bytesToMiB(bytes: number): number {
  return Math.max(1, Math.round(bytes / (1024 * 1024)))
}

function applySettingsForm(value: FileSettings): void {
  settingsForm.enabled = value.enabled
  settingsForm.allowedRoots = [...value.allowedRoots]
  settingsForm.maxUploadMiB = bytesToMiB(value.maxUploadBytes)
  settingsForm.maxListEntries = value.maxListEntries
  settingsForm.maxTextMiB = bytesToMiB(value.maxTextFileBytes)
  settingsForm.helperTimeoutSeconds = value.helperTimeoutSeconds
  settingsForm.listTimeoutSeconds = value.listTimeoutSeconds
  settingsForm.transferTimeoutSeconds = value.transferTimeoutSeconds
  settingsForm.streamIdleTimeoutSeconds = value.streamIdleTimeoutSeconds
  settingsForm.streamMaxSeconds = value.streamMaxSeconds
}

/**
 * 设置入口仅对 root 账号可见：读取成功即显示。
 * 非 root 的 403 与非 Linux 的 503 都静默隐藏，不打扰页面。
 */
async function loadSettingsAvailability(): Promise<void> {
  settingsLoading.value = true
  try {
    const value = await getFileSettings()
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

/** 保存后静默刷新家目录；关闭文件管理时该请求会 503，保持当前页面状态即可。 */
async function refreshRootsQuietly(): Promise<void> {
  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
  } catch {
    // 配置已保存，根目录刷新失败不影响抽屉结果提示。
  }
}

async function saveSettings(): Promise<void> {
  settingsSaving.value = true
  try {
    const saved = await saveFileSettings({
      enabled: settingsForm.enabled,
      allowedRoots: settingsForm.allowedRoots
        .map((root) => root.trim())
        .filter((root) => root.length > 0),
      maxUploadBytes: Math.round(settingsForm.maxUploadMiB * 1024 * 1024),
      maxListEntries: settingsForm.maxListEntries,
      maxTextFileBytes: Math.round(settingsForm.maxTextMiB * 1024 * 1024),
      helperTimeoutSeconds: settingsForm.helperTimeoutSeconds,
      listTimeoutSeconds: settingsForm.listTimeoutSeconds,
      transferTimeoutSeconds: settingsForm.transferTimeoutSeconds,
      streamIdleTimeoutSeconds: settingsForm.streamIdleTimeoutSeconds,
      streamMaxSeconds: settingsForm.streamMaxSeconds,
    })

    settingsSnapshot.value = saved
    applySettingsForm(saved)
    settingsVisible.value = false
    disabledNotice.value = !saved.enabled
    ElMessage.success('设置已保存并立即生效')

    // 从关闭状态重新启用且页面还没有页签时，重新建立首个家目录页签。
    if (saved.enabled && tabs.value.length === 0) {
      await init()
    } else {
      await refreshRootsQuietly()
    }
  } catch (error) {
    showError(error)
  } finally {
    settingsSaving.value = false
  }
}

/** 首次进入时读取家目录并创建首个页签。 */
async function init(): Promise<void> {
  loadingRoots.value = true
  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
    addTab(roots.home)
  } catch (error) {
    // 管理员在设置中关闭文件管理（或平台不支持）时返回 503，用常驻提示而不是临时 toast。
    if (error instanceof ApiError && error.status === 503) {
      disabledNotice.value = true
      return
    }

    showError(error)
  } finally {
    loadingRoots.value = false
  }
}

onMounted(() => {
  void init()
  void loadSettingsAvailability()
})
</script>

<template>
  <div class="files-view">
    <div class="toolbar">
      <span class="title">文件管理</span>
      <span class="hint">每个页签独立浏览目录；复制或剪切后切换到其它页签即可直接粘贴</span>
      <el-tooltip v-if="settingsAvailable" content="文件管理设置" placement="bottom">
        <el-button
          class="settings-button"
          :icon="Setting"
          circle
          :loading="settingsLoading"
          aria-label="文件管理设置"
          @click="openSettings"
        />
      </el-tooltip>
    </div>

    <el-alert
      v-if="disabledNotice"
      class="notice"
      type="warning"
      show-icon
      :closable="false"
      title="文件管理不可用"
      description="文件管理可能已被管理员关闭，或当前平台不支持；相关操作会返回 503，重新启用后即可继续使用。"
    />

    <!-- 页签面板保持挂载、仅切换显隐，目录位置与选中项随页签保留 -->
    <el-tabs
      v-if="tabs.length > 0"
      v-model="activeKey"
      type="card"
      editable
      class="tabs"
      @tab-add="addTab(homePath)"
      @tab-remove="removeTab"
    >
      <el-tab-pane
        v-for="tab in tabs"
        :key="tab.id"
        :label="tab.title"
        :name="tab.id"
        :closable="tabs.length > 1"
      >
        <FileBrowserPane
          :initial-path="tab.initialPath"
          @path-change="updateTabTitle(tab.id, $event)"
          @open-in-new-tab="addTab"
        />
      </el-tab-pane>
    </el-tabs>

    <div v-else v-loading="loadingRoots" class="empty">
      <el-empty :description="loadingRoots ? '正在获取家目录…' : '无法获取家目录，请重试'">
        <el-button v-if="!loadingRoots" type="primary" @click="init">重试</el-button>
      </el-empty>
    </div>

    <el-drawer
      v-model="settingsVisible"
      title="文件管理设置"
      size="480px"
      :close-on-click-modal="false"
    >
      <p class="settings-tip">
        配置保存在 SQLite 通用配置表（功能标识 files），保存后立即生效，无需重启服务。
      </p>
      <el-form label-position="top" class="settings-form">
        <el-divider content-position="left">基础</el-divider>
        <el-form-item label="启用文件管理">
          <el-switch v-model="settingsForm.enabled" />
          <div class="form-hint">关闭后所有账号（含 root）的文件操作接口都返回 503。</div>
        </el-form-item>
        <el-form-item label="允许访问的根目录">
          <el-select
            v-model="settingsForm.allowedRoots"
            class="full-width"
            multiple
            filterable
            allow-create
            default-first-option
            placeholder="输入绝对路径后按回车添加"
          >
            <el-option
              v-for="root in rootSuggestions"
              :key="root"
              :label="root"
              :value="root"
            />
          </el-select>
          <div class="form-hint">
            默认 / 表示整个文件系统；必须为绝对路径，越权仍由登录用户的系统权限兜底。
          </div>
        </el-form-item>

        <el-divider content-position="left">上限</el-divider>
        <el-form-item label="单个上传文件（MiB）">
          <el-input-number
            v-model="settingsForm.maxUploadMiB"
            class="full-width"
            :min="1"
            :max="512"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="目录列举条目上限">
          <el-input-number
            v-model="settingsForm.maxListEntries"
            class="full-width"
            :min="1"
            :max="100000"
            :step="100"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="在线编辑文本上限（MiB）">
          <el-input-number
            v-model="settingsForm.maxTextMiB"
            class="full-width"
            :min="1"
            :max="64"
            controls-position="right"
          />
          <div class="form-hint">读取与保存在线编辑内容都受此限制，过大会拖慢浏览器。</div>
        </el-form-item>

        <el-divider content-position="left">超时</el-divider>
        <el-form-item label="普通操作超时（秒）">
          <el-input-number
            v-model="settingsForm.helperTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
          <div class="form-hint">新建目录、新建文件、重命名等元数据操作。</div>
        </el-form-item>
        <el-form-item label="目录列举超时（秒）">
          <el-input-number
            v-model="settingsForm.listTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="删除/复制/移动超时（秒）">
          <el-input-number
            v-model="settingsForm.transferTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="86400"
            :step="60"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="上传/下载空闲超时（秒）">
          <el-input-number
            v-model="settingsForm.streamIdleTimeoutSeconds"
            class="full-width"
            :min="1"
            :max="3600"
            :step="10"
            controls-position="right"
          />
          <div class="form-hint">没有任何数据流动超过该时间即终止操作。</div>
        </el-form-item>
        <el-form-item label="上传/下载总时长上限（秒）">
          <el-input-number
            v-model="settingsForm.streamMaxSeconds"
            class="full-width"
            :min="0"
            :max="86400"
            :step="60"
            controls-position="right"
          />
          <div class="form-hint">0 表示不限制，仅依靠空闲超时兜底。</div>
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
.files-view {
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

.notice {
  flex: none;
  margin-bottom: 8px;
}

.tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.tabs :deep(.el-tabs__header) {
  flex: none;
  margin: 0 0 8px;
}

.tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.tabs :deep(.el-tab-pane) {
  height: 100%;
}

.empty {
  display: flex;
  flex: 1;
  align-items: center;
  justify-content: center;
  min-height: 0;
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
