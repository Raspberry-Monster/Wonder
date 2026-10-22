<script setup lang="ts">
import { reactive, ref } from "vue";
import { useRouter } from "vue-router";
import { api, fieldError } from "../api";
import { useSession } from "../session";
import type { components } from "../api.generated";
const form = reactive<components["schemas"]["Credentials"]>({
    email: "",
    password: "",
  }),
  register = ref(false),
  busy = ref(false),
  error = ref<unknown>();
const session = useSession(),
  router = useRouter();
async function submit() {
  busy.value = true;
  error.value = undefined;
  try {
    if (register.value) await api("/auth/register", "POST", form);
    await api("/auth/login", "POST", form);
    await session.load();
    await router.push("/bots");
  } catch (e) {
    error.value = e;
  } finally {
    busy.value = false;
  }
}
</script>
<template>
  <div class="login-intro">
    <span class="eyebrow">YOUR CONVERSATION COMPANION</span>
    <h1>每一次对话，<br />都有新的延续。</h1>
    <p>
      在 Wonder 中连接你的 Bot，管理记忆与模型，<br />为对话保留自己的节奏。
    </p>
    <div class="intro-note">连接 · 记忆 · 陪伴</div>
  </div>
  <a-card class="login-card" :bordered="false"
    ><span class="brand">Wonder</span>
    <h2>{{ register ? "创建你的账号" : "欢迎回来" }}</h2>
    <p class="muted">
      {{
        register
          ? "创建账号，开始配置你的 Bot。"
          : "登录后继续管理你的对话空间。"
      }}
    </p>
    <a-alert
      v-if="error"
      type="error"
      show-icon
      :title="(error as Error).message"
      class="form-alert"
    />
    <a-form layout="vertical" :model="form" @submit-success="submit">
      <a-form-item
        label="邮箱"
        field="email"
        :label-attrs="{ for: 'login-email' }"
        :rules="[{ required: true, type: 'email', message: '请输入有效邮箱' }]"
        :help="fieldError(error, 'Email')"
        :validate-status="fieldError(error, 'Email') ? 'error' : undefined"
        ><a-input
          :input-attrs="{ id: 'login-email', autocomplete: 'username' }"
          v-model="form.email"
          size="large"
      /></a-form-item>
      <a-form-item
        label="密码"
        field="password"
        :label-attrs="{ for: 'login-password' }"
        :rules="[
          { required: true, minLength: 12, message: '密码至少 12 个字符' },
        ]"
        :help="fieldError(error, 'Password')"
        :validate-status="fieldError(error, 'Password') ? 'error' : undefined"
        ><a-input-password
          :input-attrs="{
            id: 'login-password',
            autocomplete: register ? 'new-password' : 'current-password',
          }"
          v-model="form.password"
          size="large"
      /></a-form-item>
      <a-button
        type="primary"
        html-type="submit"
        block
        size="large"
        :loading="busy"
        >{{ register ? "创建账号并登录" : "登录" }}</a-button
      ></a-form
    >
    <a-button
      type="text"
      class="auth-switch"
      @click="
        register = !register;
        error = undefined;
      "
      >{{ register ? "已有账号？返回登录" : "还没有账号？注册" }}</a-button
    ></a-card
  >
</template>
