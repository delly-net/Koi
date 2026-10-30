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
      <div class="user">
        <el-tag type="success" effect="plain">{{ userName }}</el-tag>
        <el-button text @click="handleLogout">退出登录</el-button>
      </div>
    </el-header>

    <!-- 共享一级标签栏：所有已打开模块同时挂载，切换仅改变显示，状态得以保留 -->
    <el-tabs v-model="activeKey" type="card" class="tabs" @tab-remove="onTabRemove">
      <el-tab-pane
        v-for="key in workspace.tabs"
        :key="key"
        :label="tabLabel(key)"
        :name="key"
        :closable="key !== HOME_TAB"
      >
        <HomeView v-if="key === 'home'" />
        <TerminalView v-else-if="key === 'terminal'" :active="activeKey === 'terminal'" />
        <FilesView v-else-if="key === 'files'" />
        <EditorView v-else />
      </el-tab-pane>
    </el-tabs>
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
  justify-content: space-between;
  height: 56px;
  background: #ffffff;
  border-bottom: 1px solid #e4e7ed;
}

.brand {
  display: flex;
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
}

.user {
  display: flex;
  align-items: center;
  gap: 8px;
}

.tabs {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  padding: 6px 12px 0;
}

.tabs :deep(.el-tabs__header) {
  flex: none;
  margin: 0 0 6px;
}

.tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.tabs :deep(.el-tab-pane) {
  height: 100%;
}
</style>
