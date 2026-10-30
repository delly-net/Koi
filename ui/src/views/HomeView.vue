<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const auth = useAuthStore()
const userName = computed(() => auth.user?.userName ?? '')

async function handleLogout(): Promise<void> {
  await auth.logout()
  ElMessage.success('已退出登录')
  await router.replace({ name: 'login' })
}
</script>

<template>
  <el-container class="layout">
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

    <el-main class="main">
      <el-result icon="success" title="登录成功" :sub-title="`欢迎回来，${userName}`">
        <template #extra>
          <p class="hint">终端与文件管理能力已接入。</p>
        </template>
      </el-result>

      <div class="modules">
        <el-card class="module-card" shadow="hover">
          <template #header>在线终端</template>
          <p>多终端会话，基于 Linux PTY 与 WebSocket，以登录用户身份运行。</p>
          <el-button type="primary" @click="router.push({ name: 'terminal' })">打开终端</el-button>
        </el-card>

        <el-card class="module-card" shadow="hover">
          <template #header>文件管理</template>
          <p>浏览目录，新建、重命名、删除、移动、复制、粘贴、上传与下载，以登录用户身份执行。</p>
          <el-button type="primary" @click="router.push({ name: 'files' })">打开文件管理</el-button>
        </el-card>
      </div>
    </el-main>
  </el-container>
</template>

<style scoped>
.layout {
  min-height: 100%;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
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

.main {
  padding: 24px;
}

.hint {
  margin: 0;
  color: #8a919f;
}

.modules {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 16px;
  max-width: 880px;
  margin: 24px auto 0;
}

.module-card p {
  margin: 0 0 16px;
  color: #606266;
}
</style>
