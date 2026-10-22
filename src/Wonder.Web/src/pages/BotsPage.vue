<script setup lang="ts">
import { onMounted, ref } from "vue";
import { api } from "../api";
import type { components } from "../api.generated";
import EditorForm from "../components/EditorForm.vue";
const bots = ref<components["schemas"]["BotResponse"][]>([]),
  loading = ref(true),
  error = ref(""),
  creating = ref(false);
async function load() {
  loading.value = true;
  error.value = "";
  try {
    bots.value = await api("/bots");
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}
onMounted(load);
</script>
<template>
  <div class="page-heading">
    <div>
      <span class="eyebrow">WORKSPACE</span>
      <h1>我的 Bot</h1>
      <p class="muted">配置身份、连接模型，让对话自然发生。</p>
    </div>
    <a-button type="primary" size="large" @click="creating = true"
      >＋ 创建 Bot</a-button
    >
  </div>
  <a-alert v-if="error" type="error" :title="error" show-icon /><a-spin
    :loading="loading"
    ><div class="bot-grid">
      <router-link
        v-for="bot in bots"
        :key="bot.id"
        :to="`/bots/${bot.id}/settings`"
        ><a-card hoverable class="bot-card"
          ><div class="bot-symbol">
            {{ bot.name.slice(0, 1).toUpperCase() }}
          </div>
          <a-tag :color="bot.enabled ? 'green' : 'default'">{{
            bot.enabled ? "已启用" : "已暂停"
          }}</a-tag>
          <h2>{{ bot.name }}</h2>
          <p class="muted">
            每日额度 {{ bot.dailyTokenLimit.toLocaleString() }} Tokens
          </p>
          <div class="card-bottom">进入管理 <span>↗</span></div></a-card
        ></router-link
      >
    </div>
    <a-empty
      v-if="!loading && !bots.length"
      description="还没有 Bot，创建一个开始吧"
  /></a-spin>
  <a-modal
    role="dialog"
    aria-modal="true"
    v-model:visible="creating"
    title="创建 Bot"
    :footer="false"
    unmount-on-close
    ><EditorForm
      :value="{ name: '', identity: '' }"
      :fields="[
        { key: 'name', label: '名称', required: true },
        {
          key: 'identity',
          label: '身份设定',
          type: 'textarea',
          required: true,
        },
      ]"
      path="/bots"
      method="POST"
      submit-label="创建 Bot"
      @saved="
        creating = false;
        load();
      "
  /></a-modal>
</template>
