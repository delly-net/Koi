<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import {
  openTerminalSocket,
  type TerminalClientMessage,
  type TerminalServerMessage,
} from '@/api/terminal'

const props = defineProps<{
  /** 所在标签页是否处于激活状态，激活时重新校正尺寸。 */
  active: boolean
}>()

type ConnectionStatus = 'connecting' | 'connected' | 'closed' | 'error'

const hostRef = ref<HTMLDivElement | null>(null)
const status = ref<ConnectionStatus>('connecting')
const statusText = ref('正在连接终端…')
const sessionLabel = ref('')

let term: Terminal | null = null
let fitAddon: FitAddon | null = null
let socket: WebSocket | null = null
let resizeObserver: ResizeObserver | null = null
let dataSubscription: { dispose(): void } | null = null
let resizeSubscription: { dispose(): void } | null = null
let disposed = false

onMounted(async () => {
  if (!hostRef.value) {
    return
  }

  term = new Terminal({
    cursorBlink: true,
    convertEol: false,
    fontFamily: 'JetBrains Mono, Menlo, Consolas, monospace',
    fontSize: 13,
    lineHeight: 1.2,
    scrollback: 5000,
    theme: {
      background: '#1e1e1e',
      foreground: '#e6e6e6',
      cursor: '#4ec9b0',
      selectionBackground: '#264f78',
    },
  })

  fitAddon = new FitAddon()
  term.loadAddon(fitAddon)
  term.open(hostRef.value)

  // 键盘输入 → 服务端；窗口尺寸变化 → 服务端（TIOCSWINSZ）。
  dataSubscription = term.onData((data) => send({ type: 'input', data }))
  resizeSubscription = term.onResize(({ cols, rows }) => send({ type: 'resize', cols, rows }))

  resizeObserver = new ResizeObserver(() => fitTerminal())
  resizeObserver.observe(hostRef.value)

  await nextTick()
  fitTerminal()
  connect()
})

watch(
  () => props.active,
  async (active) => {
    if (!active) {
      return
    }

    await nextTick()
    fitTerminal()
    term?.focus()
  },
)

onBeforeUnmount(() => {
  disposed = true
  resizeObserver?.disconnect()
  resizeObserver = null
  dataSubscription?.dispose()
  dataSubscription = null
  resizeSubscription?.dispose()
  resizeSubscription = null
  closeSocket()
  term?.dispose()
  term = null
  fitAddon = null
})

/** 建立连接；重复调用会先关闭旧连接（用于「重新连接」）。 */
function connect(): void {
  if (disposed) {
    return
  }

  closeSocket()
  status.value = 'connecting'
  statusText.value = '正在连接终端…'

  const ws = openTerminalSocket()
  socket = ws

  ws.onmessage = (event: MessageEvent<unknown>) => {
    if (socket !== ws || typeof event.data !== 'string') {
      return
    }

    let message: TerminalServerMessage
    try {
      message = JSON.parse(event.data) as TerminalServerMessage
    } catch {
      return
    }

    handleMessage(message)
  }

  ws.onerror = () => {
    if (socket !== ws) {
      return
    }

    if (status.value === 'connecting') {
      status.value = 'error'
      statusText.value = '无法连接到终端服务，请确认已登录且服务端运行在 Linux 上。'
    }
  }

  ws.onclose = (event) => {
    if (socket !== ws) {
      return
    }

    socket = null
    if (status.value === 'connected' || status.value === 'connecting') {
      status.value = 'closed'
      statusText.value =
        event.code === 1000 ? '终端连接已关闭。' : `终端连接已断开（代码 ${event.code}）。`
      writeNotice(statusText.value)
    }
  }
}

function handleMessage(message: TerminalServerMessage): void {
  switch (message.type) {
    case 'ready':
      status.value = 'connected'
      statusText.value = ''
      sessionLabel.value = `${message.userName} · PID ${message.pid}`
      fitTerminal()
      // fit 若未改变行列，onResize 不会触发；这里主动同步一次，避免服务端停留在默认尺寸。
      if (term) {
        send({ type: 'resize', cols: term.cols, rows: term.rows })
      }
      term?.focus()
      break

    case 'output':
      term?.write(message.data)
      break

    case 'exit':
      status.value = 'closed'
      statusText.value = `终端进程已退出（退出码 ${message.code}）。`
      writeNotice(statusText.value)
      break

    case 'error':
      status.value = 'error'
      statusText.value = message.message
      break

    case 'pong':
      break
  }
}

function send(message: TerminalClientMessage): void {
  const ws = socket
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    return
  }

  ws.send(JSON.stringify(message))
}

/** 按容器尺寸重算行列；容器不可见（隐藏标签页）时跳过，避免算出 1×1。 */
function fitTerminal(): void {
  const host = hostRef.value
  if (!term || !fitAddon || !host || host.clientWidth < 24 || host.clientHeight < 24) {
    return
  }

  try {
    fitAddon.fit()
  } catch {
    // 容器尚未完成布局时 fit 可能抛错，下次尺寸变化会重试。
  }
}

function writeNotice(text: string): void {
  term?.write(`\r\n\x1b[33m[${text}]\x1b[0m\r\n`)
}

function closeSocket(): void {
  const ws = socket
  socket = null
  if (!ws) {
    return
  }

  ws.onmessage = null
  ws.onerror = null
  ws.onclose = null
  if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
    ws.close(1000, '标签页关闭')
  }
}

defineExpose({ fitTerminal })
</script>

<template>
  <div class="terminal-pane">
    <div ref="hostRef" class="terminal-host"></div>

    <div v-if="status !== 'connected'" class="terminal-overlay">
      <p class="terminal-status">{{ statusText }}</p>
      <p v-if="sessionLabel" class="terminal-meta">{{ sessionLabel }}</p>
      <el-button
        v-if="status === 'closed' || status === 'error'"
        type="primary"
        size="small"
        @click="connect"
      >
        重新连接
      </el-button>
    </div>
  </div>
</template>

<style scoped>
.terminal-pane {
  position: relative;
  height: 100%;
  min-height: 240px;
  background: #1e1e1e;
  border-radius: 4px;
  overflow: hidden;
}

.terminal-host {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  padding: 8px;
}

.terminal-host :deep(.xterm) {
  height: 100%;
}

.terminal-host :deep(.xterm-viewport) {
  background-color: transparent !important;
}

.terminal-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 24px;
  color: #e6e6e6;
  text-align: center;
  background: rgb(20 20 20 / 82%);
}

.terminal-status {
  margin: 0;
  font-size: 14px;
}

.terminal-meta {
  margin: 0;
  color: #9aa4b2;
  font-size: 12px;
}
</style>
