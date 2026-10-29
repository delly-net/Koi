<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { Lock, User } from '@element-plus/icons-vue'
import { ApiError } from '@/api/http'
import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

const formRef = ref<FormInstance>()
const form = reactive({
  userName: '',
  password: '',
})

const rules: FormRules<typeof form> = {
  userName: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

async function handleSubmit(): Promise<void> {
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) {
    return
  }

  try {
    await auth.login(form.userName.trim(), form.password)
    ElMessage.success('登录成功')

    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    await router.replace(redirect.startsWith('/') ? redirect : '/')
  } catch (error) {
    const message =
      error instanceof ApiError ? (error.detail ?? error.message) : '登录失败，请稍后重试'
    ElMessage.error(message)
  }
}
</script>

<template>
  <div class="login-page">
    <el-card class="login-card" shadow="always">
      <div class="brand">
        <img src="/koi_48.png" alt="Koi" class="brand-logo" />
        <div>
          <h1>Koi Linux Web UI</h1>
          <p>使用 Linux 系统账号登录</p>
        </div>
      </div>

      <el-form
        ref="formRef"
        :model="form"
        :rules="rules"
        label-position="top"
        @submit.prevent="handleSubmit"
      >
        <el-form-item label="用户名" prop="userName">
          <el-input
            v-model="form.userName"
            :prefix-icon="User"
            placeholder="Linux 系统账号"
            autocomplete="username"
          />
        </el-form-item>

        <el-form-item label="密码" prop="password">
          <el-input
            v-model="form.password"
            :prefix-icon="Lock"
            type="password"
            show-password
            placeholder="账号密码"
            autocomplete="current-password"
            @keyup.enter="handleSubmit"
          />
        </el-form-item>

        <el-button type="primary" native-type="submit" :loading="auth.loading" class="login-button">
          登录
        </el-button>
      </el-form>

      <p class="login-tip">认证由 Linux PAM 完成，Koi 不保存明文密码。</p>
    </el-card>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  background: linear-gradient(135deg, #eef4ff 0%, #f7f9fc 45%, #eefaf4 100%);
}

.login-card {
  width: 100%;
  max-width: 400px;
  border-radius: 16px;
}

.brand {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 24px;
}

.brand-logo {
  width: 48px;
  height: 48px;
  border-radius: 12px;
}

.brand h1 {
  margin: 0;
  font-size: 20px;
}

.brand p {
  margin: 4px 0 0;
  color: #8a919f;
  font-size: 13px;
}

.login-button {
  width: 100%;
}

.login-tip {
  margin: 16px 0 0;
  color: #8a919f;
  font-size: 12px;
  text-align: center;
}
</style>
