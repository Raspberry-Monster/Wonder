import { test, expect } from "@playwright/test";

test("management pages and forms stay centered on wide and narrow screens", async ({
  page,
}) => {
  const botId = "11111111-1111-1111-1111-111111111111";
  const bot = {
    id: botId,
    name: "布局测试",
    enabled: true,
    dailyTokenLimit: 5000000,
    monthlyTokenLimit: 50000000,
    persistMessages: true,
    messageRetentionDays: 90,
    privateIdleMinutes: 10,
    groupIdleMinutes: 10,
  };
  await page.route("**/api/v1/**", (route) => {
    const path = new URL(route.request().url()).pathname;
    const json = path.endsWith("/me")
      ? { id: "owner", email: "owner@example.com", logicalUserId: "user-1" }
      : path === "/api/v1/bots"
        ? [bot]
        : path.endsWith(botId)
          ? bot
          : path.endsWith("/binding")
            ? {
                platformAccountId: "12345",
                webSocketUrl: "ws://localhost:3001",
                enabled: true,
              }
            : path.endsWith("/memory-settings")
              ? { preferredName: "小明", allowMemory: true }
              : path.endsWith("/emotion")
                ? { state: {} }
                : [];
    return route.fulfill({ json });
  });
  const paths = [
    "/bots",
    ...[
      "settings",
      "identity",
      "binding",
      "model-routes",
      "groups",
      "context-spaces",
      "status",
    ].map((tab) => `/bots/${botId}/${tab}`),
    "/me/settings",
    "/me/memories",
    "/me/maintenance",
  ];
  for (const width of [1920, 390]) {
    await page.setViewportSize({ width, height: 1080 });
    for (const path of paths) {
      await page.goto(path);
      await expect(page.locator(".page-heading")).toBeVisible();
      await expect(page.locator(".arco-spin-loading")).toHaveCount(0);
      await page.screenshot({
        path: test
          .info()
          .outputPath(`${width}-${path.replaceAll("/", "_")}.png`),
        fullPage: true,
      });
      const layout = await page.evaluate(() => {
        const content = document
          .querySelector(".page")!
          .getBoundingClientRect();
        return {
          center: (content.left + content.right) / 2,
          viewport: window.innerWidth,
          overflow: document.documentElement.scrollWidth > window.innerWidth,
          forms: Array.from(
            document.querySelectorAll(".page .editor-form"),
          ).map((form) => {
            const rect = form.getBoundingClientRect();
            const parent = form
              .closest(".arco-card-body")!
              .getBoundingClientRect();
            return Math.abs(
              (rect.left + rect.right - parent.left - parent.right) / 2,
            );
          }),
        };
      });
      expect(
        Math.abs(layout.center - layout.viewport / 2),
        path,
      ).toBeLessThanOrEqual(1);
      expect(layout.overflow, path).toBe(false);
      for (const offset of layout.forms)
        expect(offset, path).toBeLessThanOrEqual(1);
    }
  }
});
