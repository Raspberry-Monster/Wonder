import { defineConfig } from "@playwright/test";
import config from "./playwright.config";

export default defineConfig(config, {
  testMatch: "styles.spec.ts",
  use: { baseURL: "http://127.0.0.1:5190" },
  webServer: {
    command: "npm run dev -- --port 5190 --strictPort",
    url: "http://127.0.0.1:5190",
    reuseExistingServer: false,
    timeout: 60000,
  },
});
