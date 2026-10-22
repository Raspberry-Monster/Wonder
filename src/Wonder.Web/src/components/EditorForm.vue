<script setup lang="ts">
import { reactive, ref, watch } from "vue";
import { api, fieldError } from "../api";
import { message } from "ant-design-vue";
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
    message.success("已保存");
    emit("saved");
  } catch (e) {
    error.value = e;
  } finally {
    busy.value = false;
  }
}
</script>
<template>
  <a-form layout="vertical" :model="form" @finish="save" class="editor-form">
    <a-alert
      v-if="error"
      type="error"
      show-icon
      :message="(error as Error).message"
      class="form-alert"
    />
    <a-form-item
      v-for="field in fields"
      :key="field.key"
      :label="field.label"
      :name="field.key"
      :rules="
        field.required
          ? [{ required: true, message: '请填写' + field.label }]
          : []
      "
      :validate-status="fieldError(error, field.key) ? 'error' : undefined"
      :help="fieldError(error, field.key) || field.help"
    >
      <a-switch
        v-if="field.type === 'switch'"
        v-model:checked="form[field.key]"
      />
      <a-input-number
        v-else-if="field.type === 'number'"
        v-model:value="form[field.key]"
        class="full-width"
      />
      <a-input-password
        v-else-if="field.type === 'password'"
        v-model:value="form[field.key]"
        autocomplete="new-password"
        placeholder="留空保留已有凭据"
      />
      <a-textarea
        v-else-if="field.type === 'textarea'"
        v-model:value="form[field.key]"
        :rows="6"
      />
      <a-select
        v-else-if="field.type === 'select'"
        v-model:value="form[field.key]"
        :options="field.options"
        allow-clear
      />
      <a-input v-else v-model:value="form[field.key]" /> </a-form-item
    ><a-button type="primary" html-type="submit" :loading="busy">{{
      submitLabel || "保存更改"
    }}</a-button>
  </a-form>
</template>
