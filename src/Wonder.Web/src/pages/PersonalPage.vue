<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { Message } from "@arco-design/web-vue";
import { api } from "../api";
import { useSession } from "../session";
import EditorForm, { type Field } from "../components/EditorForm.vue";
const route = useRoute(),
  router = useRouter(),
  session = useSession(),
  data = ref<any>(),
  error = ref(""),
  loading = ref(false),
  challenge = ref<any>(),
  editing = ref<any>(),
  sharing = ref<any>(),
  archived = ref(false),
  skip = ref(0);
const tab = computed(() => String(route.params.tab || "settings"));
const fields: Field[] = [
  { key: "preferredName", label: "希望如何称呼你" },
  { key: "allowMemory", label: "允许记忆", type: "switch" },
  { key: "allowCrossGroupMemory", label: "允许跨群共享", type: "switch" },
  { key: "allowMemoryMerge", label: "允许合并记忆", type: "switch" },
  { key: "allowMemoryUpdate", label: "允许更新记忆", type: "switch" },
  { key: "allowMemoryArchive", label: "允许归档记忆", type: "switch" },
  { key: "retainMemoryHistory", label: "保留记忆历史", type: "switch" },
];
async function load() {
  if (!session.user?.logicalUserId) return;
  loading.value = true;
  error.value = "";
  try {
    data.value = await api(
      "/me/" +
        (tab.value === "settings"
          ? "memory-settings"
          : tab.value === "maintenance"
            ? "memory-maintenance?skip=" + skip.value
            : `memories?skip=${skip.value}&includeArchived=${archived.value}`),
    );
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}
watch(
  () => route.fullPath,
  () => {
    skip.value = 0;
    data.value = undefined;
    void load();
  },
  { immediate: true },
);
async function action(path: string, method = "POST", body?: unknown) {
  try {
    const result = await api(path, method, body);
    Message.success("操作已完成");
    await load();
    return result;
  } catch (e) {
    Message.error((e as Error).message);
    return false;
  }
}
async function bind() {
  challenge.value = await action("/me/qq-binding-challenge");
}
async function share(memory: any) {
  try {
    const options = await api(`/me/memories/${memory.id}/sharing-options`);
    sharing.value = { memory, ...options };
  } catch (e) {
    Message.error((e as Error).message);
  }
}
async function refreshBinding() {
  await session.load();
  await load();
}
</script>
<template>
  <div class="page-heading">
    <div>
      <span class="eyebrow">PERSONAL SPACE</span>
      <h1>个人空间</h1>
      <p class="muted">决定哪些值得记住，以及如何使用这些记忆。</p>
    </div>
    <a-button @click="refreshBinding">刷新</a-button>
  </div>
  <a-card v-if="!session.user?.logicalUserId" title="连接你的 QQ 身份"
    ><p>绑定后可查看和管理对话中形成的个人记忆。</p>
    <a-button type="primary" @click="bind">生成绑定码</a-button
    ><a-alert
      v-if="challenge"
      type="info"
      :title="challenge.instruction"
      class="form-alert"
      >有效期至 {{ challenge.expiresAt }}</a-alert
    ></a-card
  >
  <template v-else
    ><a-tabs
      :active-key="tab"
      @change="(key: string | number) => router.push('/me/' + key)"
      ><a-tab-pane key="settings" title="记忆偏好" /><a-tab-pane
        key="memories"
        title="我的记忆" /><a-tab-pane key="maintenance" title="维护记录"
    /></a-tabs>
    <a-alert v-if="error" type="error" :title="error" show-icon /><a-spin
      :loading="loading"
    >
      <a-card v-if="data && tab === 'settings'" title="记忆偏好"
        ><EditorForm
          :value="data"
          :fields="fields"
          path="/me/memory-settings"
          @saved="load"
      /></a-card>
      <a-card v-if="data && tab === 'memories'" title="我的记忆"
        ><template #extra
          ><a-checkbox
            v-model="archived"
            @change="
              skip = 0;
              load();
            "
            >包含已归档</a-checkbox
          ></template
        ><a-list :data="data"
          ><template #item="{ item }"
            ><a-list-item
              ><div class="memory-body">
                <a-space
                  ><a-tag>{{ item.scope }}</a-tag
                  ><a-tag v-if="item.archivedAt">已归档</a-tag
                  ><span class="muted">{{
                    new Date(item.createdAt).toLocaleString()
                  }}</span></a-space
                >
                <p class="preserve">{{ item.text }}</p>
                <a-space wrap
                  ><a-button size="small" @click="editing = { ...item }"
                    >纠正</a-button
                  ><a-button
                    size="small"
                    :disabled="!!item.archivedAt"
                    @click="share(item)"
                    >共享</a-button
                  ><a-popconfirm
                    content="删除此记忆及关联版本？"
                    @ok="action('/me/memories/' + item.id, 'DELETE')"
                    ><a-button size="small" status="danger"
                      >删除</a-button
                    ></a-popconfirm
                  ></a-space
                >
                <div
                  v-for="copy in item.sharing"
                  :key="copy.id"
                  class="sharing-row"
                >
                  <a-tag>{{ copy.scope }}</a-tag
                  ><span>{{ copy.available ? "共享中" : "共享不可用" }}</span
                  ><a-popconfirm
                    content="撤回共享？"
                    @ok="
                      action(
                        '/me/memories/' + item.id + '/sharing/' + copy.id,
                        'DELETE',
                      )
                    "
                    ><a-button type="text" status="danger"
                      >撤回</a-button
                    ></a-popconfirm
                  >
                </div>
              </div></a-list-item
            ></template
          ></a-list
        ></a-card
      >
      <a-card v-if="data && tab === 'maintenance'" title="记忆维护记录"
        ><a-table
          row-key="id"
          :data="data"
          :pagination="false"
          :columns="[
            { title: '内容', dataIndex: 'text' },
            { title: '状态', dataIndex: 'status' },
            { title: '操作', dataIndex: 'action' },
            { title: '原因', dataIndex: 'reason' },
          ]"
          :scroll="{ x: 700 }"
      /></a-card>
      <div v-if="data && tab !== 'settings'" class="pager">
        <a-button
          :disabled="skip === 0"
          @click="
            skip -= 100;
            load();
          "
          >上一页</a-button
        ><span>{{ skip + 1 }} 起</span
        ><a-button
          :disabled="data.length < 100"
          @click="
            skip += 100;
            load();
          "
          >下一页</a-button
        >
      </div></a-spin
    ></template
  >
  <a-modal
    role="dialog"
    aria-modal="true"
    :visible="!!editing"
    title="纠正记忆"
    :footer="false"
    unmount-on-close
    @cancel="editing = undefined"
    ><EditorForm
      v-if="editing"
      :value="editing"
      :fields="[
        { key: 'text', label: '正确的内容', type: 'textarea', required: true },
      ]"
      :path="'/me/memories/' + editing.id"
      method="PATCH"
      @saved="
        editing = undefined;
        load();
      "
  /></a-modal>
  <a-modal
    role="dialog"
    aria-modal="true"
    :visible="!!sharing"
    title="共享记忆"
    :footer="false"
    @cancel="sharing = undefined"
    ><template v-if="sharing"
      ><p>{{ sharing.memory.text }}</p>
      <a-space direction="vertical"
        ><a-button
          @click="
            action('/me/memories/' + sharing.memory.id + '/sharing', 'POST', {
              scope: 'UserShared',
            }).then((result) => {
              if (result !== false) sharing = undefined;
            })
          "
          >共享到我的其他对话</a-button
        ><a-button
          v-if="sharing.space"
          @click="
            action('/me/memories/' + sharing.memory.id + '/sharing', 'POST', {
              scope: 'SpaceShared',
              contextSpaceId: sharing.space.id,
            }).then((result) => {
              if (result !== false) sharing = undefined;
            })
          "
          >共享到 {{ sharing.space.name }}</a-button
        ></a-space
      ></template
    ></a-modal
  >
</template>
