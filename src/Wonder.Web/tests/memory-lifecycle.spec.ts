import { test, expect } from "@playwright/test";

test("internal edit sends a version, preserves its retry key and shows conflicts", async ({ page }) => {
  const writes: { body: any; key?: string }[] = [];
  await page.route("**/api/v1/**", async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/me")) return route.fulfill({ json: { logicalUserId: "self" } });
    if (path.endsWith("/memory-capabilities")) return route.fulfill({ json: { edit: true, archive: true, forget: false, erase: false } });
    if (path.endsWith("/index-state")) return route.fulfill({ json: { state: "Pending" } });
    if (route.request().method() === "PATCH") {
      writes.push({ body: route.request().postDataJSON(), key: route.request().headers()["idempotency-key"] });
      return writes.length === 1 ? route.fulfill({ status: 409, json: { title: "状态已变化，请刷新后重试" } }) : route.fulfill({ json: { operationId: "receipt", factState: "Current" } });
    }
    if (path.endsWith("/memories")) return route.fulfill({ json: [{ id: "memory", text: "original", revision: 9, createdAt: "2026-10-25T00:00:00Z" }] });
    return route.fulfill({ json: [] });
  });
  await page.goto("/me/memories");
  await expect(page.getByText("索引：Pending")).toBeVisible();
  await expect(page.getByRole("button", { name: "删除", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "纠正", exact: true }).click();
  await page.getByLabel("正确的内容", { exact: true }).fill("corrected");
  await page.getByRole("dialog").getByRole("button", { name: "保存更改" }).click();
  await expect(page.getByText("状态已变化，请刷新后重试")).toBeVisible();
  await page.getByRole("dialog").getByRole("button", { name: "保存更改" }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[0].body).toEqual({ text: "corrected", expectedRevision: 9 });
  expect(writes[0].key).toBeTruthy();
  expect(writes[1].key).toBe(writes[0].key);
});

test("all lifecycle mutations stay hidden before unified release", async ({ page }) => {
  await page.route("**/api/v1/**", route => {
    const path = new URL(route.request().url()).pathname;
    const json = path.endsWith("/me") ? { logicalUserId: "self" } : path.endsWith("/memory-capabilities")
      ? { edit: false, archive: false, forget: false, erase: false } : path.endsWith("/index-state") ? { state: "Ready" }
      : path.endsWith("/memories") ? [{ id: "memory", text: "retained", revision: 1 }] : [];
    return route.fulfill({ json });
  });
  await page.goto("/me/memories");
  await expect(page.getByText("四项记忆管理操作暂未统一开放。")).toBeVisible();
  for (const name of ["纠正", "归档", "删除", "擦除关联历史"])
    await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0);
});

test("release status explains pending recovery and shows all four actions together when ready", async ({ page }) => {
  let ready = false;
  await page.route('**/api/v1/**', route => {
    const path = new URL(route.request().url()).pathname;
    const json = path.endsWith('/me') ? { logicalUserId: 'self' } : path.endsWith('/memory-capabilities')
      ? { edit: ready, archive: ready, forget: ready, erase: ready } : path.endsWith('/memory-release-status')
      ? { enabled: true, available: ready, state: ready ? 'Ready' : 'AwaitingRecoveryVerification' } : path.endsWith('/index-state')
      ? { state: 'Ready' } : path.endsWith('/memories') ? [{ id: 'memory', text: 'retained', revision: 1 }] : [];
    return route.fulfill({ json });
  });
  await page.goto('/me/memories');
  await expect(page.getByText('四项记忆管理操作暂未开放，恢复保护仍待核验。')).toBeVisible();
  for (const name of ['纠正', '归档', '删除记忆', '擦除关联历史'])
    await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
  ready = true; await page.reload();
  for (const name of ['纠正', '归档', '删除记忆', '擦除关联历史'])
    await expect(page.getByRole('button', { name, exact: true })).toBeVisible();
  await expect(page.getByText('四项记忆管理操作暂未开放，恢复保护仍待核验。')).toHaveCount(0);
});

test("Forget displays persistent partial verification and retries without claiming completion", async ({ page }) => {
  const writes: any[] = [];
  const operation = { operationId: 'op', kind: 'Forget', stage: 'NeedsReview', revision: 3, fenced: true,
    cleanupVerified: false, recoveryVerified: false, completed: false, targetMemoryId: 'memory', targetRevision: 10,
    items: [{ id: 'i', kind: 'Candidate', state: 'Completed' }, { id: 'm', kind: 'Media', state: 'NeedsReview', errorCode: 'MediaOwnershipUnprovenOrShared' }],
    uncoveredLayers: ['ReadOnlyArchive', 'HistoricalBackups', 'IndependentExports', 'ExternalQQ'] };
  await page.route('**/api/v1/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (path.endsWith('/me')) return route.fulfill({ json: { logicalUserId: 'self' } });
    if (path.endsWith('/memory-capabilities')) return route.fulfill({ json: { edit: true, archive: true, forget: true, erase: true } });
    if (path.endsWith('/index-state')) return route.fulfill({ json: { state: 'Ready' } });
    if (request.method() === 'DELETE' || path.endsWith('/retry')) { writes.push({ url: request.url(), key: request.headers()['idempotency-key'] }); return route.fulfill({ status: 202, json: operation }); }
    if (path.endsWith('/memory-operations/op')) return route.fulfill({ json: operation });
    if (path.endsWith('/memory-operations')) return route.fulfill({ json: writes.length ? [operation] : [] });
    if (path.endsWith('/memories')) return route.fulfill({ json: writes.length ? [] : [{ id: 'memory', text: 'retained', revision: 9 }] });
    return route.fulfill({ json: [] });
  });
  await page.goto('/me/memories');
  await page.getByRole('button', { name: '删除记忆', exact: true }).click();
  await expect(page.getByText('原对话、摘要与独立笔记仍保留', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: '确定', exact: true }).click();
  await expect(page.getByText('整体尚未完成 · 已隔离不等于已物理清理')).toBeVisible();
  await expect(page.getByText('独立恢复保护：待核验', { exact: false })).toBeVisible();
  await expect(page.getByText('1/2 项完成')).toBeVisible();
  expect(writes[0].url).toContain('expectedRevision=9'); expect(writes[0].key).toBeTruthy();
  await page.getByRole('button', { name: '重试未完成项目' }).click();
  await expect(page.getByText('已提交重试，请以核验进度为准')).toBeVisible();
  await expect(page.getByText('本次在线范围已核验完成')).toHaveCount(0);
  await page.reload();
  await page.getByRole('button', { name: '查看处理进度' }).click();
  await expect(page.getByText('整体尚未完成 · 已隔离不等于已物理清理')).toBeVisible();
});

test("Erase requires complete-message confirmation and binds the frozen preview scope", async ({ page }) => {
  const writes: any[] = [];
  const preview = { previewId: 'preview', scopeHash: 'frozen-hash', expectedRevision: 9, expiresAt: '2099-01-01T00:00:00Z',
    messages: [{ id: 'source', botId: 'bot', authorId: 'self', text: '颜色、食物、钢琴：整条三事实消息', sentAt: '2026-10-25T00:00:00Z' }],
    targets: [{ kind: 'Message', id: 'source', version: 'fingerprint' }] };
  await page.route('**/api/v1/**', route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (path.endsWith('/me')) return route.fulfill({ json: { logicalUserId: 'self' } });
    if (path.endsWith('/memory-capabilities')) return route.fulfill({ json: { edit: true, archive: true, forget: true, erase: true } });
    if (path.endsWith('/index-state')) return route.fulfill({ json: { state: 'Ready' } });
    if (path.endsWith('/erase-preview')) return route.fulfill({ json: preview });
    if (path.endsWith('/erase')) { writes.push({ body: request.postDataJSON(), key: request.headers()['idempotency-key'] }); return route.fulfill({ status: 409, json: { title: '预览已变化，请重新预览' } }); }
    if (path.endsWith('/memories')) return route.fulfill({ json: [{ id: 'memory', text: 'retained', revision: 9 }] });
    return route.fulfill({ json: [] });
  });
  await page.goto('/me/memories');
  await page.getByRole('button', { name: '擦除关联历史', exact: true }).click();
  await expect(page.getByText('每条被选消息中的所有事实都会丢失；首版不支持片段擦除。')).toBeVisible();
  await expect(page.getByText('颜色、食物、钢琴：整条三事实消息')).toBeVisible();
  const confirm = page.getByRole('button', { name: '确认擦除所选完整消息' });
  await expect(confirm).toBeDisabled(); expect(writes).toHaveLength(0);
  await page.getByText('我确认擦除整条所选消息，并理解同一消息中其他事实也会丢失。').click();
  await confirm.click();
  await expect(page.getByText('预览已变化，请重新预览')).toBeVisible();
  expect(writes[0].body).toEqual({ previewId: 'preview', expectedRevision: 9, scopeHash: 'frozen-hash' });
  expect(writes[0].key).toBeTruthy();
});
