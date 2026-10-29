<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import TerminalPane from '@/components/terminal/TerminalPane.vue'

interface TerminalTab {
  id: string
  title: string
}

const router = useRouter()
const tabs = ref<TerminalTab[]>([])
const activeKey = ref('')

let counter = 0

/** 新建一个终端标签；每个标签对应一条独立的 WebSocket 与 PTY。 */
function createTab(): void {
  counter += 1
  const id = `terminal-${counter}-${Date.now()}`
  tabs.value.push({ id, title: `终端 ${counter}` })
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

createTab()
</script>

<template>
  <div class="terminal-view">
    <div class="toolbar">
      <el-button text @click="router.push({ name: 'home' })">返回首页</el-button>
      <span class="title">在线终端</span>
      <span class="hint">每个标签为独立会话；断开或刷新后需重新连接</span>
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
        <TerminalPane :active="activeKey === tab.id" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<style scoped>
.terminal-view {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  height: 100vh;
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
</style>
