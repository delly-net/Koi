<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/http'
import { getSystemStatus, type SystemStatus } from '@/api/system'

const props = withDefaults(defineProps<{ active?: boolean }>(), { active: true })

/** 刷新间隔：每 5 秒拉取一次服务器状态。 */
const REFRESH_INTERVAL_MS = 5000

const status = ref<SystemStatus | null>(null)
const loading = ref(false)
const error = ref('')
const lastUpdatedAt = ref<Date | null>(null)

let timer: number | undefined
let requestInFlight = false

/** 仅首页标签可见且浏览器页面可见时轮询，避免后台标签白白消耗请求。 */
function shouldPoll(): boolean {
  return props.active && document.visibilityState === 'visible'
}

/**
 * 拉取一次状态快照。
 * 定时刷新为静默刷新：不显示 loading 遮罩，旧数据保持可见，新数据返回后直接替换；
 * loading 仅用于「重试」按钮的显式操作反馈。
 */
async function refresh(): Promise<void> {
  if (requestInFlight) {
    return
  }

  requestInFlight = true
  loading.value = true
  try {
    status.value = await getSystemStatus()
    lastUpdatedAt.value = new Date()
    error.value = ''
  } catch (caught) {
    if (caught instanceof ApiError) {
      error.value = caught.detail ? `${caught.message}：${caught.detail}` : caught.message
    } else {
      error.value = caught instanceof Error ? caught.message : '加载服务器状态失败'
    }
  } finally {
    requestInFlight = false
    loading.value = false
  }
}

function stopPolling(): void {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

/** 启动轮询：先立即刷新一次，再按 5 秒间隔刷新；不可见时只停止。 */
function startPolling(): void {
  stopPolling()
  if (!shouldPoll()) {
    return
  }

  void refresh()
  timer = window.setInterval(() => {
    void refresh()
  }, REFRESH_INTERVAL_MS)
}

function onVisibilityChange(): void {
  startPolling()
}

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibilityChange)
  startPolling()
})

watch(() => props.active, startPolling)

onBeforeUnmount(() => {
  stopPolling()
  document.removeEventListener('visibilitychange', onVisibilityChange)
})

const BYTE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

/** 字节格式化：B 与 ≥100 的数值取整，其余保留 1 位小数。 */
function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '0 B'
  }

  let size = value
  let unit = 0
  while (size >= 1024 && unit < BYTE_UNITS.length - 1) {
    size /= 1024
    unit += 1
  }

  const text = unit === 0 || size >= 100 ? Math.round(size) : size.toFixed(1)
  return `${text} ${BYTE_UNITS[unit]}`
}

function formatRate(bytesPerSecond: number): string {
  return `${formatBytes(bytesPerSecond)}/s`
}

/** 运行时长：天/小时/分钟，最小单位分钟。 */
function formatUptime(seconds: number): string {
  const totalMinutes = Math.floor(Math.max(seconds, 0) / 60)
  const days = Math.floor(totalMinutes / 1440)
  const hours = Math.floor((totalMinutes % 1440) / 60)
  const minutes = totalMinutes % 60

  if (days > 0) {
    return `${days} 天 ${hours} 小时`
  }

  if (hours > 0) {
    return `${hours} 小时 ${minutes} 分钟`
  }

  return `${minutes} 分钟`
}

function formatTime(date: Date): string {
  return date.toLocaleTimeString('zh-CN', { hour12: false })
}

/** 使用率配色：≥90% 红、≥75% 橙，其余主题蓝。 */
function progressColor(percentage: number): string {
  if (percentage >= 90) {
    return '#f56c6c'
  }

  if (percentage >= 75) {
    return '#e6a23c'
  }

  return '#409eff'
}
</script>

<template>
  <section class="server-status">
    <div class="status-head">
      <h2>服务器状态</h2>
      <span v-if="lastUpdatedAt" class="updated">
        更新于 {{ formatTime(lastUpdatedAt) }} · 每 5 秒刷新
      </span>
    </div>

    <div v-if="error" class="status-error">
      <el-alert :title="error" type="error" show-icon :closable="false" />
      <el-button text type="primary" :loading="loading" @click="refresh">重试</el-button>
    </div>

    <el-skeleton v-if="!status && !error" :rows="4" animated />

    <template v-if="status">
      <!-- 服务器名称、内核等信息先于实时指标展示，便于一眼确认目标主机。 -->
      <div class="meta">
        <span>服务器名称 {{ status.hostName }}</span>
        <span>内核 {{ status.kernelVersion }}</span>
        <span>运行 {{ formatUptime(status.uptimeSeconds) }}</span>
        <span>进程 {{ status.processCount }}</span>
      </div>

      <div class="status-grid">
        <el-card class="status-card" shadow="never">
          <template #header>
            <div class="card-head">
              <span>CPU</span>
              <span class="card-hint">{{ status.cpu.cores }} 核</span>
            </div>
          </template>
          <div class="metric">{{ status.cpu.usagePercent.toFixed(1) }}%</div>
          <el-progress
            :percentage="status.cpu.usagePercent"
            :stroke-width="8"
            :show-text="false"
            :color="progressColor"
          />
          <p class="sub">
            负载 {{ status.cpu.load1.toFixed(2) }} / {{ status.cpu.load5.toFixed(2) }} /
            {{ status.cpu.load15.toFixed(2) }}
          </p>
        </el-card>

        <el-card class="status-card" shadow="never">
          <template #header>
            <div class="card-head">
              <span>内存</span>
              <span class="card-hint">{{ formatBytes(status.memory.totalBytes) }}</span>
            </div>
          </template>
          <div class="metric">{{ status.memory.usagePercent.toFixed(1) }}%</div>
          <el-progress
            :percentage="status.memory.usagePercent"
            :stroke-width="8"
            :show-text="false"
            :color="progressColor"
          />
          <p class="sub">
            已用 {{ formatBytes(status.memory.usedBytes) }} / {{ formatBytes(status.memory.totalBytes) }}
          </p>
          <p class="sub">
            交换分区：
            <template v-if="status.memory.swapTotalBytes > 0">
              {{ formatBytes(status.memory.swapUsedBytes) }} /
              {{ formatBytes(status.memory.swapTotalBytes) }}
            </template>
            <template v-else>未启用</template>
          </p>
        </el-card>

        <el-card class="status-card" shadow="never">
          <template #header>
            <div class="card-head">
              <span>磁盘</span>
              <span class="card-hint">{{ status.disks.length }} 个挂载点</span>
            </div>
          </template>
          <p v-if="status.disks.length === 0" class="sub">未检测到磁盘</p>
          <div v-else class="disk-list">
            <div v-for="disk in status.disks" :key="disk.mountPoint" class="disk-item">
              <div class="disk-head">
                <span class="disk-name" :title="disk.mountPoint">{{ disk.mountPoint }}</span>
                <span class="disk-size">
                  {{ formatBytes(disk.usedBytes) }} / {{ formatBytes(disk.totalBytes) }}
                </span>
              </div>
              <el-progress
                :percentage="disk.usagePercent"
                :stroke-width="6"
                :show-text="false"
                :color="progressColor"
              />
            </div>
          </div>
        </el-card>

        <el-card class="status-card" shadow="never">
          <template #header>
            <div class="card-head">
              <span>网络</span>
              <span class="card-hint">全部接口</span>
            </div>
          </template>
          <div class="net-row">
            <span class="net-label">接收</span>
            <span class="net-value">↓ {{ formatRate(status.network.receivedBytesPerSecond) }}</span>
          </div>
          <div class="net-row">
            <span class="net-label">发送</span>
            <span class="net-value">↑ {{ formatRate(status.network.sentBytesPerSecond) }}</span>
          </div>
          <p class="sub">
            累计 ↓ {{ formatBytes(status.network.receivedTotalBytes) }} · ↑
            {{ formatBytes(status.network.sentTotalBytes) }}
          </p>
        </el-card>
      </div>
    </template>
  </section>
</template>

<style scoped>
.server-status {
  max-width: 1100px;
  margin: 0 auto 16px;
}

.status-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 12px;
  margin-bottom: 12px;
}

.status-head h2 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.updated {
  font-size: 12px;
  color: #8a919f;
}

.status-error {
  display: flex;
  align-items: center;
  margin-bottom: 12px;
}

.status-error :deep(.el-alert) {
  flex: 1;
}

.status-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  align-items: stretch;
  gap: 16px;
}

.status-card :deep(.el-card__header) {
  padding: 10px 16px;
}

.status-card :deep(.el-card__body) {
  padding: 12px 16px 14px;
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
  color: #303133;
}

.card-hint {
  font-size: 12px;
  font-weight: 400;
  color: #8a919f;
}

.metric {
  margin-bottom: 8px;
  font-size: 26px;
  font-weight: 600;
  line-height: 1.1;
  color: #303133;
}

.sub {
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.5;
  color: #8a919f;
}

.disk-list {
  max-height: 150px;
  overflow-y: auto;
}

.disk-item + .disk-item {
  margin-top: 8px;
}

.disk-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 4px;
  font-size: 12px;
}

.disk-name {
  overflow: hidden;
  color: #303133;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.disk-size {
  flex: none;
  color: #8a919f;
}

.net-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 6px;
}

.net-label {
  font-size: 12px;
  color: #8a919f;
}

.net-value {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.meta {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 20px;
  margin-bottom: 12px;
  padding: 10px 16px;
  font-size: 12px;
  color: #606266;
  background: #ffffff;
  border: 1px solid #e4e7ed;
  border-radius: 4px;
}
</style>
