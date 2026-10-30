<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { useWorkspaceStore } from '@/stores/workspace'

const auth = useAuthStore()
const workspace = useWorkspaceStore()
const userName = computed(() => auth.user?.userName ?? '')
</script>

<template>
  <div class="home-view">
    <el-result icon="success" title="登录成功" :sub-title="`欢迎回来，${userName}`">
      <template #extra>
        <p class="hint">终端、文件管理与文本编辑都在工作区标签中打开，可自由切换。</p>
      </template>
    </el-result>

    <div class="modules">
      <el-card class="module-card" shadow="hover">
        <template #header>在线终端</template>
        <p>多终端会话，基于 Linux PTY 与 WebSocket，以登录用户身份运行。</p>
        <el-button type="primary" class="module-action" @click="workspace.open('terminal')">打开终端</el-button>
      </el-card>

      <el-card class="module-card" shadow="hover">
        <template #header>文件管理</template>
        <p>浏览目录，新建、重命名、删除、移动、复制、粘贴、上传与下载，以登录用户身份执行。</p>
        <el-button type="primary" class="module-action" @click="workspace.open('files')">打开文件管理</el-button>
      </el-card>

      <el-card class="module-card" shadow="hover">
        <template #header>文本编辑</template>
        <p>多标签在线编辑 UTF-8 文本文件；通常从文件管理的【编辑文本】或文件名称进入。</p>
        <el-button type="primary" class="module-action" @click="workspace.open('editor')">打开文本编辑</el-button>
      </el-card>
    </div>
  </div>
</template>

<style scoped>
.home-view {
  height: 100%;
  padding: 24px;
  overflow: auto;
}

.hint {
  margin: 0;
  color: #8a919f;
}

.modules {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  align-items: stretch;
  gap: 16px;
  max-width: 1100px;
  margin: 0 auto;
}

.module-card {
  display: flex;
  flex-direction: column;
}

.module-card :deep(.el-card__body) {
  display: flex;
  flex: 1;
  flex-direction: column;
}

.module-card p {
  flex: 1;
  margin: 0 0 16px;
  color: #606266;
}

.module-action {
  width: 100%;
}
</style>
