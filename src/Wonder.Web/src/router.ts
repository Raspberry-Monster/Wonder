import { createRouter, createWebHistory } from "vue-router";
import { useSession } from "./session";
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/login", component: () => import("./pages/LoginPage.vue") },
    { path: "/", redirect: "/bots" },
    { path: "/bots", component: () => import("./pages/BotsPage.vue") },
    { path: "/bots/:id/:tab?", component: () => import("./pages/BotPage.vue") },
    { path: "/me/:tab?", component: () => import("./pages/PersonalPage.vue") },
    { path: "/system", component: () => import("./pages/SystemPage.vue") },
    { path: "/:pathMatch(.*)*", redirect: "/bots" },
  ],
});
router.beforeEach(async (to) => {
  const session = useSession();
  if (!session.loaded) {
    try {
      await session.load();
    } catch {}
  }
  if (!session.user && to.path !== "/login") return "/login";
  if (session.user && to.path === "/login") return "/bots";
});
