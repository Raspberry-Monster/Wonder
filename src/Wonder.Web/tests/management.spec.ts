import { test, expect } from "@playwright/test";

const botId = "11111111-1111-1111-1111-111111111111";
const base = `/api/v1/bots/${botId}`;

test("Arco management forms, lists and confirmation actions", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (entry) => {
    if (entry.text().includes("Content Security Policy"))
      errors.push(entry.text());
  });
  const writes: { path: string; method: string; body: any }[] = [];
  const bot = {
    id: botId,
    name: "管理测试",
    enabled: true,
    dailyTokenLimit: 5000000,
    monthlyTokenLimit: 50000000,
    persistMessages: true,
    messageRetentionDays: 90,
    privateIdleMinutes: 10,
    groupIdleMinutes: 10,
  };
  let spaces = [{ id: "space-1", name: "共享测试空间" }];
  const memory = {
    id: "memory-1",
    text: "记忆测试内容",
    scope: "Private",
    createdAt: "2026-10-22T00:00:00Z",
    sharing: [],
  };
  await page.route("**/api/v1/**", async (route) => {
    const p = new URL(route.request().url()).pathname;
    const method = route.request().method();
    const ok = (json: unknown) => route.fulfill({ json });
    if (method !== "GET") {
      const body = route.request().postDataJSON();
      writes.push({ path: p, method, body });
      if (method === "DELETE" && p.endsWith("/space-1")) spaces = [];
      return route.fulfill({ status: 204 });
    }
    if (p.endsWith("/me"))
      return ok({
        id: "owner",
        email: "owner@example.com",
        logicalUserId: "user-1",
      });
    if (p === base) return ok(bot);
    if (p.endsWith("/context-spaces")) return ok(spaces);
    if (p.endsWith("/groups"))
      return ok([
        {
          id: "group-1",
          groupId: "123456",
          respondToMention: true,
          respondToReply: true,
          commandPrefix: "/wonder",
          persistMessages: true,
          allowReflection: true,
        },
      ]);
    if (p.endsWith("/jobs"))
      return ok([
        { id: "job-1", type: "chat", status: "DeadLetter", attempts: 5 },
      ]);
    if (p.endsWith("/emotion"))
      return ok({ state: { valence: 0.5 }, narrative: { text: "今日叙事" } });
    if (p.endsWith("/memory-settings"))
      return ok({
        preferredName: "小明",
        allowMemory: true,
        allowCrossGroupMemory: false,
      });
    if (p.endsWith("/sharing-options")) return ok({});
    if (p.endsWith("/memories")) return ok([memory]);
    return ok([]);
  });

  await page.goto(`/bots/${botId}/settings`);
  await page.getByLabel("名称", { exact: true }).fill("修改名称");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(() => writes.find((w) => w.path === base)?.body.name)
    .toBe("修改名称");

  await page.locator(".arco-tabs-tab").filter({ hasText: "群组策略" }).click();
  await expect(
    page.getByRole("cell", { name: "123456", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "编辑", exact: true }).click();
  await page.getByLabel("命令前缀", { exact: true }).fill("/hello");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(
      () => writes.find((w) => w.path.endsWith("/policy"))?.body.commandPrefix,
    )
    .toBe("/hello");
  await expect(page.getByRole("dialog")).not.toBeVisible();

  await page.locator(".arco-tabs-tab").filter({ hasText: "共享空间" }).click();
  await expect(page.getByText("共享测试空间", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "删除", exact: true }).click();
  await page.getByRole("button", { name: "确定", exact: true }).click();
  await expect
    .poll(() =>
      writes.some((w) => w.method === "DELETE" && w.path.endsWith("/space-1")),
    )
    .toBe(true);

  await page.locator(".arco-tabs-tab").filter({ hasText: "运行状态" }).click();
  await expect(page.getByText("今日叙事", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "重试", exact: true }).click();
  await page.getByRole("button", { name: "确定", exact: true }).click();
  await expect
    .poll(() => writes.some((w) => w.path.endsWith("/jobs/job-1/retry")))
    .toBe(true);

  await page.getByRole("link", { name: "个人空间", exact: true }).click();
  await page.getByLabel("希望如何称呼你", { exact: true }).fill("新称呼");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(
      () =>
        writes.find((w) => w.path.endsWith("/memory-settings"))?.body
          .preferredName,
    )
    .toBe("新称呼");
  await page.locator(".arco-tabs-tab").filter({ hasText: "我的记忆" }).click();
  await expect(page.getByText(memory.text, { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "共享", exact: true }).click();
  await page.getByRole("button", { name: "共享到我的其他对话" }).click();
  await expect
    .poll(() => writes.find((w) => w.path.endsWith("/sharing"))?.body.scope)
    .toBe("UserShared");
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await page.screenshot({
    path: test.info().outputPath("personal.png"),
    fullPage: true,
  });
  expect(errors).toEqual([]);
});
