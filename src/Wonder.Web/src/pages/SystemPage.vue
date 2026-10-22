<script setup lang="ts">
import { ref } from "vue";
import { api } from "../api";
const busy = ref(""),
  error = ref(""),
  result = ref("");
async function run(kind: string) {
  busy.value = kind;
  error.value = "";
  result.value = "";
  try {
    const value = await api("/system/" + kind, "POST");
    result.value = typeof value === "string" ? value : JSON.stringify(value);
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    busy.value = "";
  }
}
</script>
<template>
  <div class="page-heading">
    <div>
      <span class="eyebrow">MAINTENANCE</span>
      <h1>系统维护</h1>
      <p class="muted">备份和删除账本操作仅对系统管理员开放。</p>
    </div>
  </div>
  <a-alert
    v-if="error"
    type="error"
    :title="error"
    show-icon
    class="form-alert"
  /><a-alert
    v-if="result"
    type="success"
    title="操作已完成"
    class="form-alert"
    >{{ result }}</a-alert
  >
  <div class="bot-grid">
    <a-card title="创建备份"
      ><p>保存数据库、密钥与删除账本的加密备份。</p>
      <a-popconfirm content="现在创建系统备份？" @ok="run('backups')"
        ><a-button
          type="primary"
          :loading="busy === 'backups'"
          :disabled="!!busy"
          >创建备份</a-button
        ></a-popconfirm
      ></a-card
    ><a-card title="导出删除账本"
      ><p>记录已执行的记忆删除，用于恢复后的删除重放。</p>
      <a-button
        :loading="busy === 'deletion-ledger'"
        :disabled="!!busy"
        @click="run('deletion-ledger')"
        >导出账本</a-button
      ></a-card
    >
  </div>
</template>
