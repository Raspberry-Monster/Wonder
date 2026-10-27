import { test, expect } from "@playwright/test";
import type { components } from "../src/api.generated";

test("Milky binding preserves connection edits during status polling", async ({
  page,
}) => {
  const botId = "11111111-1111-1111-1111-111111111111";
  let state: components["schemas"]["PlatformLoginState"] = "Connecting";
  let reads = 0;
  let submitted: unknown;
  await page.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/binding")) {
      if (request.method() === "PUT") {
        submitted = request.postDataJSON();
        state = "Online";
        return route.fulfill({ status: 204 });
      }
      reads++;
      return route.fulfill({
        json: {
          id: "binding",
          platformAccountId: "12345",
          apiBaseUrl: "http://milky:3000",
          enabled: true,
          hasAccessToken: true,
          login: { state },
        },
      });
    }
    return route.fulfill({
      json: path.endsWith("/me")
        ? { id: "owner", email: "owner@example.com" }
        : { id: botId, name: "QQ 登录测试", enabled: true },
    });
  });
  await page.goto(`/bots/${botId}/binding`);
  await expect(page.getByRole("status")).toHaveText("正在连接 Milky");
  const account = page.getByLabel("Bot QQ 号", { exact: true });
  await account.fill("67890");
  await page
    .getByLabel("Milky 服务地址", { exact: true })
    .fill("https://milky.example");
  await page.getByLabel("Milky 访问令牌", { exact: true }).fill("new-token");
  const initialReads = reads;
  state = "Disconnected";
  await expect.poll(() => reads).toBeGreaterThan(initialReads);
  await expect(page.getByRole("status")).toHaveText("Milky 已断开，等待重连");
  await expect(account).toHaveValue("67890");
  await page.getByRole("button", { name: "保存更改", exact: true }).click();
  await expect(page.getByRole("status")).toHaveText("Milky 已连接");
  await expect(page.getByAltText("QQ 登录二维码")).toHaveCount(0);
  expect(submitted).toEqual({
    platformAccountId: "67890",
    apiBaseUrl: "https://milky.example",
    accessToken: "new-token",
    clearAccessToken: false,
    enabled: true,
  });
});
