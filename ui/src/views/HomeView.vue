<script setup lang="ts">
import { useWorkspaceStore } from '@/stores/workspace'
import ServerStatus from '@/components/system/ServerStatus.vue'

/** active 由工作区传入：仅首页标签可见时才轮询服务器状态。 */
const props = withDefaults(defineProps<{ active?: boolean }>(), { active: true })

const workspace = useWorkspaceStore()
</script>

<template>
  <div class="home-view">
    <ServerStatus :active="props.active" />

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

      <el-card class="module-card" shadow="hover">
        <template #header>容器管理</template>
        <p>通过 docker 命令查看与管理容器、镜像与资源占用；仅 docker 组成员可用，手动刷新数据。</p>
        <el-button type="primary" class="module-action" @click="workspace.open('docker')">打开容器管理</el-button>
      </el-card>

      <el-card class="module-card" shadow="hover">
        <template #header>服务管理</template>
        <p>通过 systemctl 查看系统服务状态，启停、重启、重载服务，管理开机自启并查看服务日志。</p>
        <el-button type="primary" class="module-action" @click="workspace.open('service')">打开服务管理</el-button>
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

.modules {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  align-items: stretch;
  gap: 16px;
  max-width: 1400px;
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
