import { test, expect } from "@playwright/test";

test("login, custom model editing, field validation and production CSP", async ({
  page,
}) => {
  let authenticated = false;
  const violations: string[] = [];
  page.on("console", (m) => {
    if (
      m.text().includes("Content Security Policy") ||
      m.text().includes("violates the following")
    )
      violations.push(m.text());
  });
  const bot = {
    id: "11111111-1111-1111-1111-111111111111",
    name: "测试 Bot",
    enabled: true,
    dailyTokenLimit: 5000000,
    monthlyTokenLimit: 50000000,
    persistMessages: true,
    messageRetentionDays: 90,
    privateIdleMinutes: 10,
    groupIdleMinutes: 10,
  };
  let saved: any;
  let rejectFirstSave = true;
  await page.route("**/api/v1/**", async (route) => {
    const url = new URL(route.request().url()),
      p = url.pathname;
    const ok = (body: unknown) => route.fulfill({ json: body });
    if (p.endsWith("/auth/login")) {
      authenticated = true;
      return ok({ id: "owner", email: "owner@example.com" });
    }
    if (p.endsWith("/me"))
      return authenticated
        ? ok({ id: "owner", email: "owner@example.com", logicalUserId: null })
        : route.fulfill({ status: 401 });
    if (p.endsWith("/model-routes")) {
      if (route.request().method() === "PUT") {
        if (rejectFirstSave) {
          rejectFirstSave = false;
          return route.fulfill({
            status: 400,
            json: {
              title: "输入无效",
              errors: { "[0].Model": ["模型名称不可用"] },
            },
          });
        }
        saved = route.request().postDataJSON();
        return route.fulfill({ status: 204 });
      }
      return ok([]);
    }
    if (p === "/api/v1/bots") return ok([bot]);
    if (p.endsWith(bot.id)) return ok(bot);
    return ok([]);
  });
  await page.goto("/login");
  await page.getByLabel("邮箱", { exact: true }).fill("owner@example.com");
  await page.getByLabel("密码", { exact: true }).fill("a-long-test-password");
  await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByRole("heading", { name: "我的 Bot" })).toBeVisible();
  await page.getByRole("heading", { name: "测试 Bot" }).click();
  await page.getByRole("tab", { name: "模型路由" }).click();
  await page.getByRole("button", { name: "配置模型" }).click();
  await page
    .getByLabel("API 地址", { exact: true })
    .fill("http://localhost:11434/v1");
  await page.getByLabel("模型名称", { exact: true }).fill("my-custom-model");
  await page.getByLabel("API Key", { exact: true }).fill("local-key");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect(page.getByText("模型名称不可用", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect.poll(() => saved?.[0]?.model).toBe("my-custom-model");
  expect(saved[0].tokenizerId).toBe("estimate");
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await page.getByRole("button", { name: "批量应用模型" }).click();
  await page
    .getByLabel("API 地址", { exact: true })
    .fill("http://localhost:11434/v1");
  await page.getByLabel("模型名称", { exact: true }).fill("batch-model");
  await page.getByLabel("API Key", { exact: true }).fill("local-key");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect.poll(() => saved?.length).toBe(5);
  expect(
    saved.every(
      (route: any) =>
        route.model === "batch-model" && route.taskKind !== "Embedding",
    ),
  ).toBe(true);
  await expect(page.getByRole("dialog")).not.toBeVisible();
  // Strict CSP must not leave core components or modal styles blocked.
  expect(violations).toEqual([]);
  await page.screenshot({ path: "test-results/workspace.png", fullPage: true });
});

test("unauthenticated deep links redirect and API fallback stays JSON/404", async ({
  page,
  request,
}) => {
  await page.route("**/api/v1/me", (r) => r.fulfill({ status: 401 }));
  await page.goto("/bots/11111111-1111-1111-1111-111111111111/settings");
  await expect(page).toHaveURL(/\/login$/);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(
    page.getByRole("button", { name: "登录", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBeTruthy();
  const response = await request.get("/api/v1/does-not-exist");
  expect(response.status()).toBe(404);
  expect(response.headers()["content-type"] || "").not.toContain("text/html");
});
