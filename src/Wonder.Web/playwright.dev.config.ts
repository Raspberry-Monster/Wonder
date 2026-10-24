import { defineConfig } from "@playwright/test";
import config from "./playwright.config";

export default defineConfig(config, {
  outputDir: "../../TestResults/playwright-dev",
  testMatch: "styles.spec.ts",
  use: { baseURL: "http://127.0.0.1:5190" },
});
