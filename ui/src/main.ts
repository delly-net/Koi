import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'

import App from './App.vue'
import router from './router'
import './styles/index.css'
import { loadAppConfig } from './config/appConfig'

/**
 * 先加载运行时配置，再创建应用。
 * 必须在 app.use(router) 之前完成：路由守卫会在安装时立即请求 /api/auth/me，
 * 否则首屏请求会使用未配置的后端地址。
 */
async function bootstrap(): Promise<void> {
  await loadAppConfig()

  const app = createApp(App)

  app.use(createPinia())
  app.use(router)
  app.use(ElementPlus, { locale: zhCn })

  app.mount('#app')
}

void bootstrap()
