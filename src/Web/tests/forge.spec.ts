import { expect, test } from "@playwright/test";
import { clean, password, start, temp } from "../../../tests/server.mjs";

test("375px 下创建仓库、提交文件、浏览历史并管理引用", async ({ page }) => {
  const data = await temp("mingo-browser-");
  let server: Awaited<ReturnType<typeof start>> | undefined;
  const diagnostics: string[] = [];
  page.on("pageerror", error => diagnostics.push(error.stack ?? error.message));
  page.on("response", response => diagnostics.push(`${response.status()} ${new URL(response.url()).pathname}`));
  try {
    server = await start(data, 5088);
    await page.goto(server.url);
    await page.getByLabel("密码", { exact: true }).fill(password);
    await page.getByRole("button", { name: "登录", exact: true }).click();
    await page.getByRole("button", { name: "新建仓库" }).click();
    const name = page.getByLabel("仓库名称");
    const rules = page.locator("#repository-name-rules").getByRole("listitem");
    await name.fill("MinGo.GitServerTests");
    await expect(rules).toHaveText([
      "✓ 通过 · 长度为 1–63 个字符",
      "✕ 未通过 · 以小写英文字母或数字开头",
      "✕ 未通过 · 仅含小写英文字母、数字、下划线或连字符",
    ]);
    await page.getByRole("button", { name: "创建", exact: true }).click();
    await expect(name).toHaveAttribute("aria-invalid", "true");
    await name.fill("mobile-demo");
    await expect(name).toHaveAttribute("aria-invalid", "false");
    await page.getByRole("button", { name: "创建", exact: true }).click();
    await expect(page.getByRole("heading", { name: "mobile-demo" })).toBeVisible();
    await page.getByRole("button", { name: "新建文件" }).click();
    await page.getByLabel("文件路径").fill("README.md");
    await page.getByRole("textbox", { name: "文件内容" }).fill("Mobile commit");
    await page.getByLabel("提交说明").fill("Add README");
    await page.getByRole("button", { name: "提交", exact: true }).last().click();
    await expect(page.getByText("Mobile commit", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "编辑文件" }).click();
    await page.getByRole("textbox", { name: "文件内容" }).fill("Updated from mobile");
    await page.getByLabel("提交说明").fill("Update README");
    await page.getByRole("dialog").getByRole("button", { name: "提交", exact: true }).click();
    await expect(page.getByText("Updated from mobile", { exact: true })).toBeVisible();
    const history = page.getByRole("navigation").getByRole("button", { name: "提交", exact: true });
    await expect(history).toBeEnabled();
    await history.click();
    await expect(history, diagnostics.join("\n")).toHaveAttribute("aria-current", "page");
    await page.getByRole("button", { name: "Update README" }).click();
    await expect(page.locator("pre.diff")).toContainText("+Updated from mobile");
    await page.getByRole("navigation").getByRole("button", { name: "分支" }).click();
    await page.getByRole("button", { name: "新建分支" }).click();
    await page.getByLabel("名称", { exact: true }).fill("feature/mobile");
    await page.getByRole("button", { name: "创建", exact: true }).click();
    await page.getByRole("button", { name: "feature/mobile" }).click();
    await expect(page.getByText("feature/mobile", { exact: true })).toBeVisible();
    for (const width of [320, 375, 768, 1280]) {
      await page.setViewportSize({ width, height: 812 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    }
    await page.getByRole("button", { name: "Git 令牌" }).click();
    await page.getByRole("button", { name: "生成令牌" }).click();
    await expect(page.getByLabel("令牌", { exact: true })).toHaveValue(/^[a-f0-9]{64}$/);
    await page.getByRole("button", { name: "取消" }).click();
    await page.getByRole("button", { name: "退出" }).click();
    await expect(page.getByRole("heading", { name: "登录 MinGo Git" })).toBeVisible();
  } catch (error) {
    console.error(diagnostics.join("\n"));
    console.error(await page.locator("main").innerText().catch(() => ""));
    throw error;
  } finally {
    if (server) await server.stop();
    await clean(data);
  }
});
