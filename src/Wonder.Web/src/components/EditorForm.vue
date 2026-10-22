<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { api, fieldError } from "../api";
import { Message } from "@arco-design/web-vue";
export interface Field {
  key: string;
  label: string;
  type?: "number" | "switch" | "password" | "textarea" | "select";
  help?: string;
  options?: { label: string; value: any }[];
  required?: boolean;
}
const props = defineProps<{
  value: Record<string, any>;
  fields: Field[];
  path: string;
  method?: string;
  submitLabel?: string;
  transform?: (data: Record<string, any>) => unknown;
}>();
const emit = defineEmits<{ saved: [] }>();
const form = reactive<Record<string, any>>({}),
  busy = ref(false),
  error = ref<unknown>();
watch(
  () => props.value,
  (v) => {
    for (const k of Object.keys(form)) delete form[k];
    Object.assign(form, v);
    error.value = undefined;
  },
  { immediate: true, deep: true },
);
async function save() {
  busy.value = true;
  error.value = undefined;
  try {
    await api(
      props.path,
      props.method || "PUT",
      props.transform ? props.transform({ ...form }) : form,
    );
    Message.success("已保存");
    emit("saved");
  } catch (e) {
    error.value = e;
  } finally {
    busy.value = false;
  }
}
</script>
<template>
  <a-form
    layout="vertical"
    :model="form"
    @submit-success="save"
    class="editor-form"
  >
    <a-alert
      v-if="error"
      type="error"
      show-icon
      :title="(error as Error).message"
      class="form-alert"
    />
    <a-form-item
      v-for="field in fields"
      :key="field.key"
      :label="field.label"
      :field="field.key"
      :label-attrs="{ for: 'editor-' + field.key }"
      :rules="
        field.required
          ? [{ required: true, message: '请填写' + field.label }]
          : []
      "
      :validate-status="fieldError(error, field.key) ? 'error' : undefined"
      :help="fieldError(error, field.key)"
      :extra="field.help"
    >
      <a-switch
        v-if="field.type === 'switch'"
        :id="'editor-' + field.key"
        v-model="form[field.key]"
      />
      <a-input-number
        v-else-if="field.type === 'number'"
        :input-attrs="{ id: 'editor-' + field.key }"
        v-model="form[field.key]"
        class="full-width"
      />
      <a-input-password
        v-else-if="field.type === 'password'"
        :input-attrs="{
          id: 'editor-' + field.key,
          autocomplete: 'new-password',
        }"
        v-model="form[field.key]"
        placeholder="留空保留已有凭据"
      />
      <a-textarea
        v-else-if="field.type === 'textarea'"
        :textarea-attrs="{ id: 'editor-' + field.key }"
        v-model="form[field.key]"
        :auto-size="{ minRows: 6, maxRows: 14 }"
      />
      <a-select
        v-else-if="field.type === 'select'"
        :id="'editor-' + field.key"
        role="combobox"
        :aria-label="field.label"
        v-model="form[field.key]"
        :options="field.options"
        allow-clear
      />
      <a-input
        v-else
        :input-attrs="{ id: 'editor-' + field.key }"
        v-model="form[field.key]"
      /> </a-form-item
    ><a-button type="primary" html-type="submit" :loading="busy">{{
      submitLabel || "保存更改"
    }}</a-button>
  </a-form>
</template>
