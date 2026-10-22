import { defineConfig, devices } from "@playwright/test";
export default defineConfig({
  testDir: "tests",
  testMatch: "*.spec.ts",
  use: { baseURL: "http://127.0.0.1:5189", trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command:
      "dotnet run --project ../Wonder.Host --no-build --no-launch-profile --urls http://127.0.0.1:5189",
    url: "http://127.0.0.1:5189/health/live",
    reuseExistingServer: !process.env.CI,
    timeout: 60000,
    env: {
      Workers__Enabled: "false",
      ConnectionStrings__Postgres:
        "Host=127.0.0.1;Port=1;Database=wonder_browser_test;Username=unused;Password=unused",
    },
  },
});
