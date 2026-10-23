<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { Message } from "@arco-design/web-vue";
import { api } from "../api";
import type { components } from "../api.generated";
import EditorForm, { type Field } from "../components/EditorForm.vue";
const route = useRoute(),
  router = useRouter(),
  bot = ref<any>(),
  data = ref<any>(),
  spaces = ref<any[]>([]),
  tokenizers = ref<components["schemas"]["TokenizerResponse"][]>([]),
  loading = ref(false),
  error = ref(""),
  editing = ref<any>(),
  skip = ref(0),
  batch = ref(false),
  selectedTasks = ref<string[]>([]);
const id = computed(() => String(route.params.id)),
  base = computed(() => `/bots/${id.value}`),
  tab = computed(() => String(route.params.tab || "settings"));
const tabs = [
  ["settings", "基本设置"],
  ["identity", "身份设定"],
  ["binding", "平台连接"],
  ["model-routes", "模型路由"],
  ["groups", "群组策略"],
  ["context-spaces", "共享空间"],
  ["status", "运行状态"],
];
const tasks = [
  "Chat",
  "LightReflection",
  "StrongReflection",
  "DailyNarrative",
  "RollingSummary",
  "Embedding",
];
const taskNames: Record<string, string> = {
  Chat: "聊天",
  LightReflection: "轻量反思",
  StrongReflection: "深度反思",
  DailyNarrative: "每日叙事",
  RollingSummary: "滚动摘要",
  Embedding: "向量嵌入",
};
const settingsFields: Field[] = [
  { key: "name", label: "名称", required: true },
  { key: "enabled", label: "启用 Bot", type: "switch" },
  { key: "persistMessages", label: "保留消息", type: "switch" },
  { key: "messageRetentionDays", label: "消息保留天数", type: "number" },
  {
    key: "privateIdleMinutes",
    label: "私聊会话空闲时间（分钟）",
    type: "number",
  },
  {
    key: "groupIdleMinutes",
    label: "群聊会话空闲时间（分钟）",
    type: "number",
  },
  { key: "dailyTokenLimit", label: "每日 Token 额度", type: "number" },
  { key: "monthlyTokenLimit", label: "每月 Token 额度", type: "number" },
];
const modelFields = computed<Field[]>(() => [
  {
    key: "taskKind",
    label: "用途",
    type: "select",
    options: tasks.map((value) => ({ value, label: taskNames[value]! })),
    required: true,
  },
  { key: "endpoint", label: "API 地址", required: true },
  { key: "model", label: "模型名称", required: true },
  { key: "apiKey", label: "API Key", type: "password" },
  {
    key: "contextWindow",
    label: "上下文窗口",
    type: "number",
    help: "按模型实际限制填写；向量用途表示单次输入上限。",
  },
  { key: "maxOutputTokens", label: "最大输出 Tokens", type: "number" },
  { key: "supportsImages", label: "支持图片", type: "switch" },
  { key: "supportsJson", label: "支持 JSON 输出", type: "switch" },
  { key: "supportsTools", label: "支持工具调用（聊天）", type: "switch" },
  {
    key: "thinkingEnabled",
    label: "思考模式",
    type: "select",
    options: [
      { label: "开启", value: "enabled" },
      { label: "关闭", value: "disabled" },
    ],
    help: "清空表示使用供应商默认设置",
  },
  {
    key: "reasoningEffort",
    label: "思考强度",
    help: "按模型填写，例如 low、medium、high、max；留空使用默认值",
  },
  { key: "embeddingDimensions", label: "向量维度", type: "number" },
  {
    key: "tokenizerId",
    label: "Tokenizer",
    type: "select",
    options: [
      { value: "auto", label: "自动匹配" },
      { value: "estimate", label: "使用估算" },
      ...tokenizers.value.map((item) => ({ value: item.id, label: item.id })),
    ],
    help: "自动匹配当前模型的词表；未匹配时使用估算。也可手动选择。",
    required: true,
  },
  {
    key: "messageTokenOverhead",
    label: "每条消息的格式预留 Tokens",
    type: "number",
  },
  { key: "imageTokenBudget", label: "每张图片预留 Tokens", type: "number" },
  {
    key: "inputCostPerMillion",
    label: "每百万输入 Tokens 成本",
    type: "number",
  },
  {
    key: "outputCostPerMillion",
    label: "每百万输出 Tokens 成本",
    type: "number",
  },
]);
const bindingFields: Field[] = [
  { key: "platformAccountId", label: "Bot QQ 号", required: true },
  { key: "webSocketUrl", label: "OneBot WebSocket 地址", required: true },
  { key: "accessToken", label: "Access Token", type: "password" },
  { key: "enabled", label: "启用连接", type: "switch" },
];
const groupFields = computed<Field[]>(() => [
  { key: "respondToMention", label: "响应 @", type: "switch" },
  { key: "respondToReply", label: "响应引用回复", type: "switch" },
  { key: "commandPrefix", label: "命令前缀" },
  { key: "persistMessages", label: "保留消息", type: "switch" },
  { key: "allowReflection", label: "允许反思", type: "switch" },
  {
    key: "contextSpaceId",
    label: "共享空间",
    type: "select",
    options: spaces.value.map((s) => ({ label: s.name, value: s.id })),
  },
]);
async function load() {
  loading.value = true;
  error.value = "";
  try {
    bot.value = await api(base.value);
    if (tab.value === "status") {
      const [jobs, usage, emotion, deliveries] = await Promise.all([
        api(base.value + `/jobs?skip=${skip.value}`),
        api(base.value + "/usage"),
        api(base.value + "/emotion"),
        api(base.value + "/deliveries"),
      ]);
      data.value = { jobs, usage, emotion, deliveries };
    } else if (tab.value === "model-routes") {
      const [routes, available] = await Promise.all([
        api(base.value + "/model-routes"),
        api<components["schemas"]["TokenizerResponse"][]>(
          base.value + "/model-routes/tokenizers",
        ),
      ]);
      data.value = routes;
      tokenizers.value = available;
    } else if (tab.value === "settings") data.value = bot.value;
    else data.value = await api(base.value + "/" + tab.value);
    if (tab.value === "groups")
      spaces.value = await api(base.value + "/context-spaces");
  } catch (e) {
    error.value = (e as Error).message;
  } finally {
    loading.value = false;
  }
}
watch(
  () => route.fullPath,
  () => {
    editing.value = undefined;
    data.value = undefined;
    skip.value = 0;
    void load();
  },
  { immediate: true },
);
function newModel() {
  batch.value = false;
  editing.value = {
    taskKind:
      tasks.find((t) => !data.value?.some((r: any) => r.taskKind === t)) ||
      "Chat",
    endpoint: "",
    model: "",
    apiKey: "",
    contextWindow: 8192,
    maxOutputTokens: 1024,
    supportsImages: false,
    supportsJson: false,
    supportsTools: false,
    thinkingEnabled: null,
    reasoningEffort: null,
    embeddingDimensions: 1536,
    tokenizerId: "auto",
    messageTokenOverhead: 16,
    imageTokenBudget: 4096,
    inputCostPerMillion: 0,
    outputCostPerMillion: 0,
  };
}
function newBatch() {
  newModel();
  batch.value = true;
  selectedTasks.value = tasks.filter((t) => t !== "Embedding");
}
function editModel(record: any) {
  batch.value = false;
  editing.value = {
    ...record,
    apiKey: "",
    thinkingEnabled:
      record.thinkingEnabled === true
        ? "enabled"
        : record.thinkingEnabled === false
          ? "disabled"
          : undefined,
  };
}
function modelPayload(v: Record<string, any>) {
  const targets = batch.value ? selectedTasks.value : [v.taskKind];
  if (!targets.length) throw new Error("请选择至少一个用途");
  return targets.map((taskKind) => ({
    ...v,
    taskKind,
    supportsTools: taskKind === "Chat" && v.supportsTools,
    thinkingEnabled:
      v.thinkingEnabled === "enabled"
        ? true
        : v.thinkingEnabled === "disabled"
          ? false
          : null,
    reasoningEffort: v.reasoningEffort || null,
  }));
}
function newGroup() {
  editing.value = {
    groupId: "",
    respondToMention: true,
    respondToReply: true,
    commandPrefix: "/wonder",
    persistMessages: true,
    allowReflection: true,
    proactiveEnabled: false,
    contextSpaceId: null,
  };
}
async function action(path: string, method = "POST", body?: unknown) {
  try {
    await api(base.value + path, method, body);
    Message.success("操作已完成");
    await load();
  } catch (e) {
    Message.error((e as Error).message);
  }
}
const jobColumns = [
  { title: "任务", dataIndex: "type" },
  { title: "状态", dataIndex: "status" },
  { title: "尝试次数", dataIndex: "attempts" },
  { title: "错误", dataIndex: "errorCode" },
  { title: "创建时间", dataIndex: "createdAt" },
  { title: "操作", slotName: "action" },
];
</script>
<template>
  <a-breadcrumb
    ><a-breadcrumb-item
      ><router-link to="/bots">我的 Bot</router-link></a-breadcrumb-item
    ><a-breadcrumb-item>{{
      bot?.name || "Bot"
    }}</a-breadcrumb-item></a-breadcrumb
  >
  <div class="page-heading">
    <div>
      <h1>{{ bot?.name || "Bot 管理" }}</h1>
      <p class="muted">管理连接、身份与对话方式。</p>
    </div>
    <a-button @click="load" :loading="loading">刷新</a-button>
  </div>
  <a-tabs
    :active-key="tab"
    @change="(key: string | number) => router.push(`/bots/${id}/${key}`)"
    ><a-tab-pane v-for="[key, label] in tabs" :key="key" :title="label"
  /></a-tabs>
  <a-alert
    v-if="error"
    type="error"
    show-icon
    :title="error"
    class="form-alert"
  /><a-spin :loading="loading">
    <a-card v-if="data && tab === 'settings'" title="基本设置"
      ><EditorForm
        :value="data"
        :fields="settingsFields"
        :path="base"
        method="PATCH"
        @saved="load"
    /></a-card>
    <a-card v-if="data && tab === 'identity'" title="身份设定"
      ><EditorForm
        :value="{ content: data[0]?.content || '' }"
        :fields="[
          {
            key: 'content',
            label: '身份与表达方式',
            type: 'textarea',
            required: true,
          },
        ]"
        :path="base + '/identity'"
        submit-label="发布新版本"
        @saved="load"
      /><a-divider /><a-collapse
        ><a-collapse-item
          v-for="revision in data"
          :key="revision.id"
          :header="`版本 ${revision.revision}`"
          ><p class="preserve">{{ revision.content }}</p></a-collapse-item
        ></a-collapse
      ></a-card
    >
    <a-card v-if="!loading && tab === 'binding'" title="平台连接"
      ><EditorForm
        :value="{
          platformAccountId: '',
          webSocketUrl: '',
          enabled: true,
          ...data,
          accessToken: '',
        }"
        :fields="bindingFields"
        :path="base + '/binding'"
        @saved="load"
    /></a-card>
    <a-card v-if="data && tab === 'model-routes'" title="模型路由"
      ><template #extra
        ><a-space
          ><a-button @click="newBatch">批量应用模型</a-button
          ><a-button type="primary" @click="newModel"
            >配置模型</a-button
          ></a-space
        ></template
      ><a-alert
        type="info"
        show-icon
        title="每个用途可连接不同供应商。Tokenizer 与模型独立配置，最终用量按供应商返回结果结算。"
        class="form-alert"
      /><a-table
        row-key="id"
        :data="data"
        :pagination="false"
        :columns="[
          { title: '用途', dataIndex: 'taskKind', slotName: 'taskKind' },
          { title: '模型', dataIndex: 'model' },
          { title: 'Tokenizer', dataIndex: 'tokenizerId' },
          {
            title: '实际词表 / estimate 表示估算',
            dataIndex: 'resolvedTokenizer',
          },
          { title: '操作', slotName: 'action' },
        ]"
        :scroll="{ x: 650 }"
        ><template #taskKind="{ record }">{{
          taskNames[record.taskKind] || record.taskKind
        }}</template
        ><template #action="{ record }"
          ><a-button type="text" @click="editModel(record)"
            >编辑</a-button
          ></template
        ></a-table
      ></a-card
    >
    <a-card v-if="data && tab === 'groups'" title="群组策略"
      ><template #extra
        ><a-button type="primary" @click="newGroup"
          >添加群组</a-button
        ></template
      ><a-table
        row-key="id"
        :data="data"
        :columns="[
          { title: '群号', dataIndex: 'groupId' },
          { title: '命令前缀', dataIndex: 'commandPrefix' },
          { title: '操作', slotName: 'action' },
        ]"
        ><template #action="{ record }"
          ><a-button type="text" @click="editing = { ...record }"
            >编辑</a-button
          ></template
        ></a-table
      ></a-card
    >
    <a-card v-if="data && tab === 'context-spaces'" title="共享空间"
      ><template #extra
        ><a-button type="primary" @click="editing = { name: '' }"
          >创建空间</a-button
        ></template
      ><a-list :data="data"
        ><template #item="{ item }"
          ><a-list-item
            ><a-list-item-meta
              :title="item.name"
              :description="item.id"
            /><template #actions
              ><a-button type="text" @click="editing = { ...item }"
                >重命名</a-button
              ><a-popconfirm
                content="删除空间并撤销关联的共享上下文？"
                @ok="action('/context-spaces/' + item.id, 'DELETE')"
                ><a-button type="text" status="danger"
                  >删除</a-button
                ></a-popconfirm
              ></template
            ></a-list-item
          ></template
        ></a-list
      ></a-card
    >
    <div v-if="data && tab === 'status'" class="stack">
      <a-card title="近 30 天用量"
        ><a-table
          row-key="taskKind"
          :data="data.usage"
          :pagination="false"
          :columns="[
            { title: '用途', dataIndex: 'taskKind' },
            { title: '输入 Tokens', dataIndex: 'inputTokens' },
            { title: '输出 Tokens', dataIndex: 'outputTokens' },
            { title: '成本', dataIndex: 'cost' },
            { title: '请求数', dataIndex: 'requests' },
            { title: '估算用量请求数', dataIndex: 'estimatedRequests' },
          ]"
          :scroll="{ x: 600 }"
      /></a-card>
      <a-card title="情绪与每日叙事"
        ><a-descriptions :column="3"
          ><a-descriptions-item
            v-for="key in ['valence', 'arousal', 'energy']"
            :key="key"
            :label="
              (
                {
                  valence: '情绪倾向',
                  arousal: '活跃度',
                  energy: '精力',
                } as any
              )[key]
            "
            >{{ data.emotion.state?.[key] }}</a-descriptions-item
          ></a-descriptions
        >
        <p class="preserve">
          {{ data.emotion.narrative?.text || "暂无每日叙事" }}
        </p></a-card
      >
      <a-card title="任务记录"
        ><a-table
          row-key="id"
          :data="data.jobs"
          :columns="jobColumns"
          :pagination="false"
          :scroll="{ x: 850 }"
          ><template #action="{ record }"
            ><a-popconfirm
              v-if="record.status === 'DeadLetter'"
              content="重新执行此任务？"
              @ok="action('/jobs/' + record.id + '/retry')"
              ><a-button type="text">重试</a-button></a-popconfirm
            ></template
          ></a-table
        >
        <div class="pager">
          <a-button
            :disabled="skip === 0"
            @click="
              skip -= 100;
              load();
            "
            >上一页</a-button
          ><span>{{ skip + 1 }} 起</span
          ><a-button
            :disabled="data.jobs.length < 100"
            @click="
              skip += 100;
              load();
            "
            >下一页</a-button
          >
        </div></a-card
      >
      <a-card title="待确认投递"
        ><a-list :data="data.deliveries"
          ><template #item="{ item }"
            ><a-list-item
              ><a-list-item-meta
                :title="item.status"
                :description="item.id"
              /><template #actions
                ><a-popconfirm
                  content="确认该消息已发送？"
                  @ok="
                    action('/deliveries/' + item.id + '/resolve', 'POST', {
                      action: 'confirm-sent',
                    })
                  "
                  ><a-button>确认已发送</a-button></a-popconfirm
                ><a-popconfirm
                  content="重发可能产生重复消息，继续？"
                  @ok="
                    action('/deliveries/' + item.id + '/resolve', 'POST', {
                      action: 'retry',
                    })
                  "
                  ><a-button status="danger">重试</a-button></a-popconfirm
                ></template
              ></a-list-item
            ></template
          ></a-list
        ></a-card
      >
    </div></a-spin
  >
  <a-modal
    role="dialog"
    aria-modal="true"
    :visible="!!editing"
    :title="
      tab === 'model-routes'
        ? '模型配置'
        : tab === 'groups'
          ? '群组策略'
          : '共享空间'
    "
    :footer="false"
    :width="680"
    unmount-on-close
    @cancel="editing = undefined"
  >
    <a-form-item
      v-if="editing && tab === 'model-routes' && batch"
      label="应用到生成任务"
      ><a-checkbox-group
        v-model="selectedTasks"
        :options="
          tasks
            .filter((t) => t !== 'Embedding')
            .map((value) => ({ value, label: taskNames[value] }))
        "
    /></a-form-item>
    <EditorForm
      v-if="editing && tab === 'model-routes'"
      :value="editing"
      :fields="
        batch ? modelFields.filter((f) => f.key !== 'taskKind') : modelFields
      "
      :path="base + '/model-routes'"
      :transform="modelPayload"
      @saved="
        editing = undefined;
        load();
      "
    />
    <a-form-item
      v-if="editing && tab === 'groups'"
      label="目标群号"
      :label-attrs="{ for: 'group-id' }"
      ><a-input :input-attrs="{ id: 'group-id' }" v-model="editing.groupId"
    /></a-form-item>
    <EditorForm
      v-if="editing && tab === 'groups'"
      :value="editing"
      :fields="groupFields"
      :path="
        base + '/groups/' + encodeURIComponent(editing.groupId) + '/policy'
      "
      :transform="(v) => ({ ...v, contextSpaceId: v.contextSpaceId || null })"
      @saved="
        editing = undefined;
        load();
      "
    />
    <EditorForm
      v-if="editing && tab === 'context-spaces'"
      :value="editing"
      :fields="[{ key: 'name', label: '空间名称', required: true }]"
      :path="base + '/context-spaces' + (editing.id ? '/' + editing.id : '')"
      :method="editing.id ? 'PUT' : 'POST'"
      @saved="
        editing = undefined;
        load();
      "
    />
  </a-modal>
</template>
