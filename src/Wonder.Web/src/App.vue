<script setup lang="ts">
import { computed, onMounted, onUnmounted } from "vue";
import { useRoute, useRouter } from "vue-router";
import zhCN from "@arco-design/web-vue/es/locale/lang/zh-cn";
import { useSession } from "./session";
const route = useRoute(),
  router = useRouter(),
  session = useSession();
const active = computed(() =>
  route.path.startsWith("/bots")
    ? "bots"
    : route.path.startsWith("/me")
      ? "me"
      : "system",
);
const unauthorized = () => {
  session.user = null;
  void router.push("/login");
};
onMounted(() => window.addEventListener("wonder:unauthorized", unauthorized));
onUnmounted(() =>
  window.removeEventListener("wonder:unauthorized", unauthorized),
);
async function logout() {
  await session.logout();
  await router.push("/login");
}
</script>
<template>
  <a-config-provider :locale="zhCN">
    <div v-if="route.path === '/login'" class="login-layout">
      <router-view />
    </div>
    <a-layout v-else class="shell"
      ><a-layout-header class="topbar">
        <router-link to="/bots" class="brand"
          ><span class="brand-mark">W</span> Wonder</router-link
        >
        <nav aria-label="主导航">
          <router-link to="/bots" :class="{ active: active === 'bots' }"
            >我的 Bot</router-link
          ><router-link to="/me/settings" :class="{ active: active === 'me' }"
            >个人空间</router-link
          ><router-link to="/system" :class="{ active: active === 'system' }"
            >系统维护</router-link
          >
        </nav>
        <div class="account">
          <span>{{ session.user?.email }}</span
          ><a-button @click="logout">退出</a-button>
        </div></a-layout-header
      >
      <a-layout-content class="page"><router-view /></a-layout-content
      ><a-layout-footer class="footer"
        >Wonder · 让每一次对话有所延续</a-layout-footer
      ></a-layout
    >
  </a-config-provider>
</template>
