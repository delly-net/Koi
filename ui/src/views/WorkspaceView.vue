<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { useEditorStore } from '@/stores/editor'
import {
  HOME_TAB,
  WORKSPACE_TAB_TITLES,
  useWorkspaceStore,
  type WorkspaceTabKey,
} from '@/stores/workspace'
import HomeView from '@/views/HomeView.vue'
import TerminalView from '@/views/TerminalView.vue'
import FilesView from '@/views/FilesView.vue'
import EditorView from '@/views/EditorView.vue'

const router = useRouter()
const auth = useAuthStore()
const editor = useEditorStore()
const workspace = useWorkspaceStore()

const userName = computed(() => auth.user?.userName ?? '')

/** el-tabs 的 v-model 可能是 string | number，这里统一收敛为标签 key。 */
const activeKey = computed({
  get: () => workspace.activeKey,
  set: (value: string | number) => {
    workspace.activate(String(value) as WorkspaceTabKey)
  },
})

/** 编辑器存在未保存修改时在标签标题上标记，避免切走后忘记保存。 */
function tabLabel(key: WorkspaceTabKey): string {
  const dirty = key === 'editor' && editor.tabs.some((tab) => tab.content !== tab.savedContent)
  return dirty ? `${WORKSPACE_TAB_TITLES[key]} ●` : WORKSPACE_TAB_TITLES[key]
}

function onTabRemove(name: string | number): void {
  workspace.close(String(name) as WorkspaceTabKey)
}

/** 标签是否已打开：决定面板挂载，关闭标签即卸载面板（终端 WS/PTY 随之释放）。 */
function hasTab(key: WorkspaceTabKey): boolean {
  return workspace.tabs.includes(key)
}

async function handleLogout(): Promise<void> {
  await auth.logout()
  ElMessage.success('已退出登录')
  await router.replace({ name: 'login' })
}
</script>

<template>
  <div class="workspace">
    <el-header class="header">
      <div class="brand">
        <img src="/koi_48.png" alt="Koi" class="logo" />
        <span class="title">Koi Linux Web UI</span>
      </div>

      <!-- 一级标签嵌入 logo 栏：这里只保留导航头，面板在下方 .panels 中挂载 -->
      <el-tabs v-model="activeKey" class="header-tabs" @tab-remove="onTabRemove">
        <el-tab-pane
          v-for="key in workspace.tabs"
          :key="key"
          :label="tabLabel(key)"
          :name="key"
          :closable="key !== HOME_TAB"
        />
      </el-tabs>

      <div class="user">
        <el-tag type="success" effect="plain">{{ userName }}</el-tag>
        <el-button text @click="handleLogout">退出登录</el-button>
      </div>
    </el-header>

    <!-- 面板容器：按标签是否打开决定挂载，切换仅改变显示，状态得以保留 -->
    <div class="panels">
      <HomeView v-if="hasTab('home')" v-show="activeKey === 'home'" />
      <TerminalView
        v-if="hasTab('terminal')"
        v-show="activeKey === 'terminal'"
        :active="activeKey === 'terminal'"
      />
      <FilesView v-if="hasTab('files')" v-show="activeKey === 'files'" />
      <EditorView v-if="hasTab('editor')" v-show="activeKey === 'editor'" />
    </div>
  </div>
</template>

<style scoped>
.workspace {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #f5f7fa;
}

.header {
  display: flex;
  flex: none;
  align-items: center;
  gap: 16px;
  height: 56px;
  padding: 0 12px;
  background: #ffffff;
  border-bottom: 1px solid #e4e7ed;
}

.brand {
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
}

.logo {
  width: 32px;
  height: 32px;
  border-radius: 8px;
}

.title {
  font-size: 16px;
  font-weight: 600;
  white-space: nowrap;
}

/* 标签导航占用中间剩余空间；内容区隐藏，面板已移到 .panels 承载 */
.header-tabs {
  flex: 1;
  min-width: 0;
}

.header-tabs :deep(.el-tabs__header) {
  margin: 0;
}

.header-tabs :deep(.el-tabs__nav-wrap::after) {
  display: none;
}

.header-tabs :deep(.el-tabs__content) {
  display: none;
}

.header-tabs :deep(.el-tabs__item) {
  height: 34px;
  padding: 0 16px;
  font-size: 14px;
  line-height: 34px;
}

.header-tabs :deep(.el-tabs__nav-prev),
.header-tabs :deep(.el-tabs__nav-next) {
  height: 34px;
  line-height: 34px;
}

.user {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
}

.panels {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
</style>
