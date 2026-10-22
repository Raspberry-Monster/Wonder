import { defineStore } from "pinia";
import { api } from "./api";
import type { components } from "./api.generated";
export const useSession = defineStore("session", {
  state: () => ({
    user: null as components["schemas"]["AccountResponse"] | null,
    loaded: false,
  }),
  actions: {
    async load() {
      try {
        this.user = await api("/me");
      } finally {
        this.loaded = true;
      }
    },
    async logout() {
      await api("/auth/logout", "POST");
      this.user = null;
    },
  },
});
