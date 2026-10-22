import { test, expect } from "@playwright/test";

test("Ant Design styles apply to the login form", async ({ page }) => {
  await page.route("**/api/v1/me", (route) => route.fulfill({ status: 401 }));
  await page.goto("/login");
  const button = page.getByRole("button", { name: "登录", exact: true });
  await expect(button).toHaveCSS("background-color", "rgb(40, 104, 91)");
  await expect(button).toHaveCSS("height", "40px");
  await expect(page.getByLabel("邮箱", { exact: true })).toHaveCSS(
    "border-top-style",
    "solid",
  );
  await page.screenshot({
    path: test.info().outputPath("login.png"),
    fullPage: true,
  });
});
