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
  const memory = {
    id: "memory-1",
    text: "记忆测试内容",
    createdAt: "2026-10-22T00:00:00Z",
  };
  await page.route("**/api/v1/**", async (route) => {
    const p = new URL(route.request().url()).pathname;
    const method = route.request().method();
    const ok = (json: unknown) => route.fulfill({ json });
    if (method !== "GET") {
      const body = route.request().postDataJSON();
      writes.push({ path: p, method, body });
      return route.fulfill({ status: 204 });
    }
    if (p.endsWith("/me"))
      return ok({
        id: "owner",
        email: "owner@example.com",
        logicalUserId: "user-1",
      });
    if (p === base) return ok(bot);
    if (p.endsWith("/groups"))
      return ok([
        {
          id: "group-1",
          groupId: "123456",
          respondToMention: true,
          respondToReply: true,
          commandPrefix: "/wonder",
          persistMessages: true,
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
      });
    if (p.endsWith("/notes/settings")) return ok({ allowNotes: false });
    if (p.endsWith("/notes/bots")) return ok([{ id: botId, name: "管理测试" }]);
    if (p.endsWith("/notes/note-1/history"))
      return ok([
        { version: 1, content: "已经纠正的旧笔记", reason: "旧判断" },
        { version: 2, content: "当前用户笔记", reason: "已纠正" },
      ]);
    if (p.endsWith("/notes"))
      return ok([{ id: "note-1", botId, content: "当前用户笔记", version: 2 }]);
    if (p.endsWith("/reflection-runs"))
      return ok([
        {
          id: "run-1",
          createdAt: "2026-10-24T12:00:00Z",
          status: "ReflectionCompleted",
          taskStatus: "Completed",
          requests: [{ priority: "Normal", reason: "整理当前对话" }],
          downstream: [{ type: "memory", status: "Pending" }],
        },
      ]);
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
  await expect(page.getByLabel("允许反思", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("共享空间", { exact: true })).toHaveCount(0);
  await page.getByLabel("命令前缀", { exact: true }).fill("/hello");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(
      () => writes.find((w) => w.path.endsWith("/policy"))?.body.commandPrefix,
    )
    .toBe("/hello");
  const groupWrite = writes.find((w) => w.path.endsWith("/policy"))!.body;
  expect(groupWrite).not.toHaveProperty("contextSpaceId");
  expect(groupWrite).not.toHaveProperty("allowReflection");
  expect(groupWrite).not.toHaveProperty("proactiveEnabled");
  await expect(page.getByRole("dialog")).not.toBeVisible();

  await expect(
    page.locator(".arco-tabs-tab").filter({ hasText: "共享空间" }),
  ).toHaveCount(0);

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
  await expect(
    page.getByRole("button", { name: "共享", exact: true }),
  ).toHaveCount(0);
  await page
    .locator(".arco-tabs-tab")
    .filter({ hasText: "Notes 授权" })
    .click();
  await expect(
    page.getByText("Miku 的笔记可以包含观察和猜测", { exact: false }),
  ).toBeVisible();
  await page.getByRole("switch").click();
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(
      () =>
        writes.find((w) => w.path.endsWith("/notes/settings"))?.body.allowNotes,
    )
    .toBe(true);
  await page.getByRole("button", { name: "彻底删除关联 Notes" }).click();
  await page.getByRole("button", { name: "确定", exact: true }).click();
  await expect
    .poll(() =>
      writes.some(
        (w) => w.method === "DELETE" && w.path.endsWith("/notes/personal-data"),
      ),
    )
    .toBe(true);
  await page.locator(".arco-tabs-tab").filter({ hasText: "我的笔记" }).click();
  await page.getByRole("button", { name: "修订历史", exact: true }).click();
  await expect(page.getByRole("dialog").locator("del")).toHaveText(
    "已经纠正的旧笔记",
  );
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Close", exact: true })
    .click();
  await page.getByRole("button", { name: "新建笔记", exact: true }).click();
  await page
    .getByLabel("笔记内容", { exact: true })
    .fill("这是用户明确保存的笔记");
  await page.getByRole("button", { name: "保存更改" }).click();
  await expect
    .poll(() => writes.find((w) => w.path.endsWith("/notes"))?.body.content)
    .toBe("这是用户明确保存的笔记");
  await page.locator(".arco-tabs-tab").filter({ hasText: "整理记录" }).click();
  await expect(
    page.getByText("memory：Pending", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "清除本条原因", exact: true }).click();
  await page.getByRole("button", { name: "确定", exact: true }).click();
  await expect
    .poll(() =>
      writes.some(
        (w) =>
          w.method === "DELETE" &&
          w.path.endsWith("/reflection-runs/run-1/reason"),
      ),
    )
    .toBe(true);
  await page.screenshot({
    path: test.info().outputPath("personal.png"),
    fullPage: true,
  });
  expect(errors).toEqual([]);
  expect(writes.some((w) => /sharing|context-spaces/.test(w.path))).toBe(false);
});
