import { defineConfig } from "@playwright/test";

const baseURL = "http://127.0.0.1:5173";

export default defineConfig({
  testDir: "./tests",
  timeout: 20_000,
  fullyParallel: false,
  forbidOnly: true,
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL,
    trace: "retain-on-failure"
  },
  webServer: {
    command: "node tests/fixtures/proxy-harness.mjs",
    url: baseURL,
    reuseExistingServer: false,
    timeout: 20_000,
    stdout: "pipe",
    stderr: "pipe"
  },
  projects: [
    {
      name: "unit",
      testMatch: /.*\.unit\.spec\.ts/
    },
    {
      name: "chromium",
      testMatch: /.*\.browser\.spec\.ts/,
      use: { browserName: "chromium" }
    },
    {
      name: "accessibility",
      testMatch: /.*\.a11y\.spec\.ts/,
      use: { browserName: "chromium" }
    }
  ]
});
