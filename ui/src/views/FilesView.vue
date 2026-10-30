<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { getFileRoots } from '@/api/files'
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
let counter = 0

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

/** 首次进入时读取家目录并创建首个页签。 */
async function init(): Promise<void> {
  loadingRoots.value = true
  try {
    const roots = await getFileRoots()
    homePath.value = roots.home
    addTab(roots.home)
  } catch (error) {
    showError(error)
  } finally {
    loadingRoots.value = false
  }
}

onMounted(init)
</script>

<template>
  <div class="files-view">
    <div class="toolbar">
      <span class="title">文件管理</span>
      <span class="hint">每个页签独立浏览目录；复制或剪切后切换到其它页签即可直接粘贴</span>
    </div>

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
</style>
