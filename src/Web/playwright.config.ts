import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  workers: 1,
  timeout: 60_000,
  use: {
    browserName: "chromium", channel: process.env.MINGO_TEST_BROWSER_CHANNEL ?? "chromium", viewport: { width: 375, height: 812 },
    isMobile: true, hasTouch: true, actionTimeout: 10_000,
  },
});
