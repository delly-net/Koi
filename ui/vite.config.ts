import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      // 开发期把 /api 代理到后端，浏览器视角保持同源，Cookie 可直接生效。
      // 仅当 conf/setting.json 的 baseUrl 为空串（同源）时生效；配置为完整地址时请求直连后端。
      '/api': {
        target: 'http://127.0.0.1:5000',
        changeOrigin: false,
        // 终端使用 WebSocket，开发期同样经 Vite 转发以保持同源 Cookie。
        ws: true,
      },
    },
  },
})
