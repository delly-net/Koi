<script setup lang="ts">
import type { Component } from 'vue'
import { Box, EditPen, FolderOpened, Monitor, SetUp } from '@element-plus/icons-vue'
import { useWorkspaceStore, type WorkspaceTabKey } from '@/stores/workspace'
import ServerStatus from '@/components/system/ServerStatus.vue'

/** active 由工作区传入：仅首页标签可见时才轮询服务器状态。 */
const props = withDefaults(defineProps<{ active?: boolean }>(), { active: true })

const workspace = useWorkspaceStore()

/** 首页功能入口：上图标、下名称；描述仅在悬停图标时通过 Tooltip 展示。 */
interface HomeModule {
  key: Exclude<WorkspaceTabKey, 'home'>
  label: string
  description: string
  icon: Component
}

const MODULES: HomeModule[] = [
  {
    key: 'terminal',
    label: '在线终端',
    description: '多终端会话，基于 Linux PTY 与 WebSocket，以登录用户身份运行。',
    icon: Monitor,
  },
  {
    key: 'files',
    label: '文件管理',
    description: '浏览目录，新建、重命名、删除、移动、复制、粘贴、上传与下载，以登录用户身份执行。',
    icon: FolderOpened,
  },
  {
    key: 'editor',
    label: '文本编辑',
    description: '多标签在线编辑 UTF-8 文本文件；通常从文件管理的【编辑文本】或文件名称进入。',
    icon: EditPen,
  },
  {
    key: 'docker',
    label: '容器管理',
    description: '通过 docker 命令查看与管理容器、镜像与资源占用；仅 docker 组成员可用，手动刷新数据。',
    icon: Box,
  },
  {
    key: 'service',
    label: '服务管理',
    description: '通过 systemctl 查看系统服务状态，启停、重启、重载服务，管理开机自启并查看服务日志。',
    icon: SetUp,
  },
]

/** 打开入口：复用工作区标签逻辑，已打开时直接激活而不会重复打开。 */
function openModule(module: HomeModule): void {
  workspace.open(module.key)
}
</script>

<template>
  <div class="home-view">
    <ServerStatus :active="props.active" />

    <div class="modules">
      <div
        v-for="module in MODULES"
        :key="module.key"
        class="module-tile"
        role="button"
        tabindex="0"
        :aria-label="`${module.label}：${module.description}`"
        @click="openModule(module)"
        @keydown.enter.prevent="openModule(module)"
        @keydown.space.prevent="openModule(module)"
      >
        <el-tooltip
          :content="module.description"
          placement="top"
          effect="dark"
          :show-after="200"
          popper-class="module-tip"
        >
          <el-icon class="module-icon" :size="40">
            <component :is="module.icon" />
          </el-icon>
        </el-tooltip>
        <span class="module-name">{{ module.label }}</span>
      </div>
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
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 16px;
  max-width: 1100px;
  margin: 0 auto;
}

/* 轻量磁贴：默认无边框，悬停/键盘聚焦时浅色高亮并轻微上浮 */
.module-tile {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 24px 16px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: #ffffff;
  cursor: pointer;
  user-select: none;
  transition:
    border-color 0.2s ease,
    background-color 0.2s ease,
    box-shadow 0.2s ease,
    transform 0.2s ease;
}

.module-tile:hover,
.module-tile:focus-visible {
  border-color: var(--el-color-primary-light-5);
  background: var(--el-color-primary-light-9);
  box-shadow: 0 4px 12px rgba(64, 158, 255, 0.12);
  transform: translateY(-2px);
}

.module-tile:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}

.module-icon {
  color: var(--el-color-primary);
}

.module-name {
  font-size: 14px;
  color: #303133;
}

/* Tooltip 挂载到 body，scoped 样式需用 :global 命中 */
:global(.module-tip) {
  max-width: 260px;
  line-height: 1.6;
}
</style>
