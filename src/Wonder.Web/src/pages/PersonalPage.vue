<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
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
  archived = ref(false),
  skip = ref(0);
const noteEditor = ref<any>(),
  noteBots = ref<any[]>([]),
  noteHistory = ref<any>();
const capabilities = ref({ edit: false, archive: false, forget: false, erase: false });
const releaseStatus = ref<{ state?: string }>({});
const indexStates = ref<Record<string, string>>({});
const archiveKeys = new Map<string, string>();
const privacyKeys = new Map<string, string>();
const privacyOperations = ref<any[]>([]), privacyOperation = ref<any>(), erasePreview = ref<any>(), privacyBusy = ref(false);
const eraseSelected = ref<string[]>([]), eraseAcknowledged = ref(false);
let progressTimer: ReturnType<typeof setTimeout> | undefined;
onUnmounted(() => { if (progressTimer) clearTimeout(progressTimer); });
const itemLabel: Record<string, string> = { Memory: '记忆版本', Candidate: '记忆提案', ReflectionMemory: '反思中的记忆子项', ReflectionDraft: '记忆草稿', SummaryDraft: '摘要草稿', SummaryProjection: '安全投影', Message: '完整消息', Summary: '关联摘要', Media: '媒体', OutgoingPart: '出站分段' };
const stateLabel: Record<string, string> = { Completed: '已核验完成', Pending: '待处理', Processing: '处理中', Failed: '失败 · 保持隔离', NeedsReview: '待核验 · 保持隔离', Fenced: '已隔离', Deleting: '清理中', PendingCleanup: '清理或恢复保护待完成' };
const layerLabel: Record<string, string> = { IndependentNotesAndRelationships: '独立 Notes / 关系正文', ReadOnlyArchive: '只读归档', HistoricalBackups: '历史备份', IndependentExports: '独立导出', ExternalQQ: '外部 QQ 已发送内容' };
const selectionMatchesPreview = computed(() => erasePreview.value && JSON.stringify([...eraseSelected.value].sort()) === JSON.stringify(erasePreview.value.receipt.messages.map((m: any) => m.id).sort()));
const tab = computed(() => String(route.params.tab || "settings"));
const fields: Field[] = [
  { key: "preferredName", label: "希望如何称呼你" },
  { key: "allowMemory", label: "允许记忆", type: "switch" },
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
          : tab.value === "notes"
            ? "notes/settings"
            : tab.value === "user-notes"
              ? "notes"
              : tab.value === "reflection"
                ? "reflection-runs?skip=" + skip.value
                : tab.value === "maintenance"
                  ? "memory-maintenance?skip=" + skip.value
                  : `memories?skip=${skip.value}&includeArchived=${archived.value}`),
    );
    if (tab.value === "user-notes")
      noteBots.value = await api("/me/notes/bots");
    if (tab.value === "memories") {
      capabilities.value = await api("/me/memory-capabilities");
      releaseStatus.value = await api("/me/memory-release-status");
      const states = await Promise.all(data.value.map(async (item: any) => [item.id, (await api('/me/memories/' + item.id + '/index-state')).state]));
      indexStates.value = Object.fromEntries(states);
      privacyOperations.value = await api('/me/memory-operations');
    }
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
async function refreshBinding() {
  await session.load();
  await load();
}
async function loadNoteHistory(note: any) {
  try {
    noteHistory.value = {
      currentVersion: note.version,
      revisions: await api("/me/notes/" + note.id + "/history"),
    };
  } catch (e) {
    Message.error((e as Error).message);
  }
}
async function archiveMemory(item: any) {
  const target = item.id + ':' + item.revision;
  const key = archiveKeys.get(target) ?? crypto.randomUUID();
  archiveKeys.set(target, key);
  try {
    await api('/me/memories/' + item.id + '/archive', 'POST', { expectedRevision: item.revision }, { 'Idempotency-Key': key });
    Message.success('已归档');
    await load();
  } catch (e) { Message.error((e as Error).message); }
}
function privacyKey(target: string) {
  const key = privacyKeys.get(target) ?? crypto.randomUUID();
  privacyKeys.set(target, key); return key;
}
async function showProgress(operation: any, attempt = 0) {
  privacyOperation.value = operation;
  if (progressTimer) clearTimeout(progressTimer);
  if (!operation.completed && operation.stage !== 'NeedsReview' && attempt < 15)
    progressTimer = setTimeout(async () => {
      try { await showProgress(await api('/me/memory-operations/' + operation.operationId), attempt + 1); }
      catch (e) { Message.error((e as Error).message); }
    }, 2000);
}
function closeProgress() { privacyOperation.value = undefined; if (progressTimer) clearTimeout(progressTimer); }
async function retryPrivacy() {
  try {
    await showProgress(await api('/me/memory-operations/' + privacyOperation.value.operationId + '/retry', 'POST'));
    Message.info('已提交重试，请以核验进度为准');
  } catch (e) { Message.error((e as Error).message); }
}
async function forgetMemory(item: any) {
  privacyBusy.value = true;
  try {
    const result = await api('/me/memories/' + item.id + '?expectedRevision=' + item.revision, 'DELETE', undefined,
      { 'Idempotency-Key': privacyKey('Forget:' + item.id + ':' + item.revision) });
    await showProgress(result); await load();
  } catch (e) { Message.error((e as Error).message); }
  finally { privacyBusy.value = false; }
}
async function previewErase(item: any, refine = false) {
  if (refine && eraseSelected.value.length === 0) return;
  privacyBusy.value = true;
  try {
    const receipt = await api('/me/memories/' + item.id + '/erase-preview', 'POST', {
      expectedRevision: item.revision, messageIds: refine ? eraseSelected.value : undefined,
    });
    erasePreview.value = { target: item, receipt, available: refine ? erasePreview.value.available : receipt.messages };
    eraseSelected.value = receipt.messages.map((m: any) => m.id); eraseAcknowledged.value = false;
  } catch (e) { Message.error((e as Error).message); }
  finally { privacyBusy.value = false; }
}
async function confirmErase() {
  if (!eraseAcknowledged.value || !selectionMatchesPreview.value) return;
  const value = erasePreview.value; privacyBusy.value = true;
  try {
    const result = await api('/me/memories/' + value.target.id + '/erase', 'POST', {
      previewId: value.receipt.previewId, expectedRevision: value.receipt.expectedRevision, scopeHash: value.receipt.scopeHash,
    }, { 'Idempotency-Key': privacyKey('Erase:' + value.receipt.previewId) });
    erasePreview.value = undefined; await showProgress(result); await load();
  } catch (e) { Message.error((e as Error).message); }
  finally { privacyBusy.value = false; }
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
        title="我的记忆" /><a-tab-pane
        key="maintenance"
        title="维护记录" /><a-tab-pane
        key="notes"
        title="Notes 授权" /><a-tab-pane
        key="user-notes"
        title="我的笔记" /><a-tab-pane key="reflection" title="整理记录"
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
      <a-card v-if="data && tab === 'notes'" title="独立 Notes 授权">
        <p>
          Miku
          的笔记可以包含观察和猜测，可能过时或不准确，与正式记忆冲突时需要确认。Miku
          自写笔记不直接展示。
        </p>
        <EditorForm
          :value="data"
          :fields="[
            { key: 'allowNotes', label: '允许关联我的 Notes', type: 'switch' },
          ]"
          path="/me/notes/settings"
          @saved="load"
        />
        <a-popconfirm
          content="彻底删除所有与我相关的 Notes、旧版本和衍生内容？"
          @ok="action('/me/notes/personal-data', 'DELETE')"
        >
          <a-button status="danger">彻底删除关联 Notes</a-button>
        </a-popconfirm>
      </a-card>
      <a-card v-if="data && tab === 'reflection'" title="后台整理记录">
        <a-list :data="data"
          ><template #item="{ item }"
            ><a-list-item
              ><div>
                <p>
                  {{ new Date(item.createdAt).toLocaleString() }} · 反思：{{
                    item.status
                  }}
                  · 任务：{{ item.taskStatus }}
                </p>
                <p v-for="(r, i) in item.requests" :key="i">
                  {{ r.priority }}：{{ r.reason }}
                </p>
                <a-popconfirm
                  v-if="item.requests?.some((r: any) => r.reason)"
                  content="仅清除这条整理原因正文，保留整理结果、次数和任务记录？"
                  @ok="
                    action(
                      '/me/reflection-runs/' + item.id + '/reason',
                      'DELETE',
                    )
                  "
                  ><a-button type="text">清除本条原因</a-button></a-popconfirm
                >
                <p v-for="(d, i) in item.downstream" :key="i">
                  {{ d.type }}：{{ d.status }}
                </p>
                <p v-for="(m, i) in item.memoryMaintenance" :key="i">
                  记忆维护：{{ m.status }}（{{ m.count }}）
                </p>
                <p v-if="item.cancellationReason">
                  {{ item.cancellationReason }}
                </p>
              </div></a-list-item
            ></template
          ></a-list
        >
      </a-card>
      <a-card v-if="data && tab === 'user-notes'" title="我明确保存的笔记">
        <p>这里只展示你自己保存的笔记。Miku 自写的 Notes 不直接展示。</p>
        <a-button
          type="primary"
          :disabled="noteBots.length === 0"
          @click="noteEditor = { botId: noteBots[0]?.id, content: '' }"
          >新建笔记</a-button
        >
        <a-list :data="data"
          ><template #item="{ item }"
            ><a-list-item
              ><div>
                <p class="preserve">{{ item.content }}</p>
                <a-space
                  ><a-button
                    @click="
                      noteEditor = { ...item, expectedVersion: item.version }
                    "
                    >修改</a-button
                  ><a-button @click="loadNoteHistory(item)">修订历史</a-button>
                  <a-popconfirm
                    content="移除这条用户笔记？保留修订历史。"
                    @ok="
                      action(
                        '/me/notes/' +
                          item.id +
                          '?expectedVersion=' +
                          item.version,
                        'DELETE',
                      )
                    "
                    ><a-button status="danger">移除</a-button></a-popconfirm
                  ></a-space
                >
              </div></a-list-item
            ></template
          ></a-list
        >
      </a-card>
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
        ><a-alert v-if="!capabilities.edit" type="info" :title="releaseStatus.state === 'AwaitingRecoveryVerification' ? '四项记忆管理操作暂未开放，恢复保护仍待核验。' : '四项记忆管理操作暂未统一开放。'" />
        <a-list :data="data"
          ><template #item="{ item }"
            ><a-list-item
              ><div class="memory-body">
                <a-space
                  ><a-tag v-if="item.archivedAt">已归档</a-tag
                  ><a-tag v-if="item.isOutdated">过去认知 · 已被取代</a-tag
                  ><a-tag v-if="!item.archivedAt">当前事实</a-tag
                  ><a-tag v-if="indexStates[item.id]">索引：{{ indexStates[item.id] }}</a-tag
                  ><span class="muted">{{
                    new Date(item.createdAt).toLocaleString()
                  }}</span></a-space
                >
                <p class="preserve">{{ item.text }}</p>
                <a-space wrap
                  ><a-button v-if="capabilities.edit && !item.archivedAt" size="small" @click="editing = { ...item, expectedRevision: item.revision }"
                    >纠正</a-button
                  ><a-popconfirm v-if="capabilities.archive && !item.archivedAt"
                    content="将此记忆退出当前事实检索？原对话、摘要和独立笔记保留。"
                    @ok="archiveMemory(item)"
                    ><a-button size="small"
                      >归档</a-button
                    ></a-popconfirm
                  ><a-button v-if="capabilities.edit && ['Failed', 'Retrying'].includes(indexStates[item.id] ?? '')" size="small" @click="action('/me/memories/' + item.id + '/retry-index')">重试索引</a-button
                  ><a-popconfirm v-if="capabilities.forget" content="删除这条 Memory 及关联版本；原对话、摘要与独立笔记仍保留，相同内容可能仍存在于历史记录中。若要擦除原消息，请先选择擦除关联历史。" @ok="forgetMemory(item)"><a-button size="small" status="danger" :loading="privacyBusy">删除记忆</a-button></a-popconfirm
                  ><a-button v-if="capabilities.erase" size="small" status="danger" :loading="privacyBusy" @click="previewErase(item)">擦除关联历史</a-button
                  ></a-space
                >
              </div></a-list-item
            ></template
          ></a-list
        ></a-card
      >
      <a-card v-if="tab === 'memories' && privacyOperations.length" title="隐私操作记录">
        <a-list :data="privacyOperations"><template #item="{ item }"><a-list-item>
          <a-space wrap><span>{{ item.kind === 'Erase' ? '擦除关联历史' : '删除记忆' }} · {{ stateLabel[item.stage] || item.stage }}</span>
            <a-button @click="showProgress(item)">查看处理进度</a-button>
            <a-button v-if="item.kind === 'Forget' && capabilities.erase && item.targetRevision > 0" @click="previewErase({ id: item.targetMemoryId, revision: item.targetRevision })">擦除保留的关联历史</a-button></a-space>
        </a-list-item></template></a-list>
      </a-card>
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
      <div
        v-if="data && ['memories', 'maintenance', 'reflection'].includes(tab)"
        class="pager"
      >
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
      idempotent
      :transform="(value: any) => ({ text: value.text, expectedRevision: value.expectedRevision })"
      @saved="
        editing = undefined;
        load();
      "
  /></a-modal>
  <a-modal
    :visible="!!noteEditor"
    role="dialog"
    aria-modal="true"
    title="用户笔记"
    :footer="false"
    @cancel="noteEditor = undefined"
    unmount-on-close
  >
    <EditorForm
      v-if="noteEditor"
      :value="noteEditor"
      :fields="[
        {
          key: 'botId',
          label: '对话角色',
          type: 'select',
          options: noteBots.map((b) => ({ label: b.name, value: b.id })),
          required: true,
        },
        { key: 'content', label: '笔记内容', type: 'textarea', required: true },
        { key: 'reason', label: '修订原因' },
      ]"
      :path="'/me/notes' + (noteEditor.id ? '/' + noteEditor.id : '')"
      :method="noteEditor.id ? 'PUT' : 'POST'"
      @saved="
        noteEditor = undefined;
        load();
      "
    />
  </a-modal>
  <a-modal :visible="!!erasePreview" title="确认擦除完整消息" :footer="false" unmount-on-close @cancel="erasePreview = undefined">
    <template v-if="erasePreview">
      <a-alert type="warning" title="每条被选消息中的所有事实都会丢失；首版不支持片段擦除。" />
      <p>仅处理本人同 Bot 私聊中经核验的消息及预览列出的派生资源。独立 Notes、关系正文、只读归档、历史备份、独立导出和外部 QQ 分别保留或报告未覆盖。</p>
      <a-checkbox-group v-model="eraseSelected" direction="vertical">
        <a-checkbox v-for="m in erasePreview.available" :key="m.id" :value="m.id">
          <span>Bot：{{ m.botId }} · 作者（本人）：{{ m.authorId }} · {{ new Date(m.sentAt).toLocaleString() }}</span>
          <p class="preserve">{{ m.text }}</p>
        </a-checkbox>
      </a-checkbox-group>
      <a-button v-if="!selectionMatchesPreview" :disabled="!eraseSelected.length" :loading="privacyBusy" @click="previewErase(erasePreview.target, true)">更新所选范围预览</a-button>
      <p>确认期限：{{ new Date(erasePreview.receipt.expiresAt).toLocaleString() }}；本次范围共 {{ erasePreview.receipt.targets.length }} 个资源。</p>
      <a-checkbox v-model="eraseAcknowledged">我确认擦除整条所选消息，并理解同一消息中其他事实也会丢失。</a-checkbox>
      <a-button status="danger" :loading="privacyBusy" :disabled="!eraseAcknowledged || !selectionMatchesPreview || !eraseSelected.length" @click="confirmErase">确认擦除所选完整消息</a-button>
    </template>
  </a-modal>
  <a-modal :visible="!!privacyOperation" title="隐私处理进度" :footer="false" @cancel="closeProgress">
    <template v-if="privacyOperation">
      <a-alert :type="privacyOperation.completed ? 'success' : 'warning'" :title="privacyOperation.completed ? '本次在线范围已核验完成' : '整体尚未完成 · 已隔离不等于已物理清理'" />
      <p>隔离：{{ privacyOperation.fenced ? '已建立' : '未建立' }}；副载荷核验：{{ privacyOperation.cleanupVerified ? '完成' : '待完成' }}；独立恢复保护：{{ privacyOperation.recoveryVerified ? '已核验' : '待核验' }}</p>
      <p>{{ privacyOperation.items.filter((i: any) => i.state === 'Completed').length }}/{{ privacyOperation.items.length }} 项完成</p>
      <a-list :data="privacyOperation.items"><template #item="{ item }"><a-list-item>{{ itemLabel[item.kind] || item.kind }}：{{ stateLabel[item.state] || item.state }}<span v-if="item.errorCode"> · {{ item.errorCode }}</span></a-list-item></template></a-list>
      <p>本次未覆盖：{{ privacyOperation.uncoveredLayers.map((l: string) => layerLabel[l] || l).join('、') }}。已发送或在途 QQ 内容不代表已撤回。</p>
      <a-space><a-button @click="api('/me/memory-operations/' + privacyOperation.operationId).then(value => showProgress(value))">刷新真实状态</a-button>
        <a-button v-if="!privacyOperation.completed && capabilities.forget" @click="retryPrivacy">重试未完成项目</a-button></a-space>
    </template>
  </a-modal>
  <a-modal
    :visible="!!noteHistory"
    role="dialog"
    aria-modal="true"
    title="用户笔记修订历史"
    :footer="false"
    @cancel="noteHistory = undefined"
    unmount-on-close
  >
    <a-list v-if="noteHistory" :data="noteHistory.revisions"
      ><template #item="{ item }"
        ><a-list-item
          ><div>
            <a-tag>{{
              item.version === noteHistory.currentVersion
                ? "当前版本"
                : "旧版 · 不再用于当前判断"
            }}</a-tag>
            <p class="preserve">
              <del v-if="item.version !== noteHistory.currentVersion">{{
                item.content
              }}</del
              ><span v-else>{{ item.content }}</span>
            </p>
            <p class="muted">{{ item.reason }}</p>
          </div></a-list-item
        ></template
      ></a-list
    >
  </a-modal>
</template>
