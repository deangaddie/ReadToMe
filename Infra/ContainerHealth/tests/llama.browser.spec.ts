import { expect, test, type Page } from "@playwright/test";

test.beforeEach(async ({ request }) => {
  await request.post("/proxy/llama/llama-mode?mode=success");
  await request.post("/proxy/llama/restart-service?service=llama");
});

async function openPrepared(page: Page): Promise<void> {
  await page.goto("/detail.html?service=llama");
  await expect(page.getByRole("combobox", { name: "Model preset" })).toHaveValue("gemma-loaded");
  await expect(page.getByRole("button", { name: "Run Llama completion" })).toBeEnabled();
}

test("Llama prepares on open, sends the selected preset through the real proxy, streams both channels, and refreshes models", async ({ page, request }) => {
  await openPrepared(page);
  await expect(page.getByRole("combobox", { name: "Model preset" }).locator("option")).toHaveText(["gemma-sleeping — Sleeping", "gemma-loaded — Loaded", "gemma-failed — Failed"]);
  await page.getByLabel("Prompt").fill("Explain streaming");
  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("Additional request properties").fill('{"reasoning_format":"auto","seed":9}');
  await page.getByRole("button", { name: "Run Llama completion" }).click();
  await expect(page.getByTestId("live-thinking")).toContainText("think 💡");
  await expect(page.getByTestId("live-answer")).toContainText("streamed answer");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toContainText("Thinking");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toContainText("streamed answer");
  await expect(page.getByRole("button", { name: "Cancel run" })).toHaveCount(0);
  await expect.poll(async () => (await request.get("/proxy/llama/llama-events").then((response) => response.json()) as { modelCalls: number }).modelCalls).toBeGreaterThanOrEqual(2);
  const events = await request.get("/proxy/llama/llama-events").then((response) => response.json()) as { requests: unknown[] };
  expect(events.requests.at(-1)).toEqual({
    temperature: 0.8, top_p: 0.95, max_tokens: 256, frequency_penalty: 0, presence_penalty: 0,
    reasoning_format: "auto", seed: 9, model: "gemma-loaded", messages: [{ role: "user", content: "Explain streaming" }], stream: true
  });
});

test("manual readiness preparation locks Run, preserves a valid selection, and falls back when it disappears", async ({ page, request }) => {
  await openPrepared(page);
  const models = page.getByRole("combobox", { name: "Model preset" });
  await models.selectOption("gemma-sleeping");
  await request.post("/proxy/llama/llama-mode?mode=models-slow");
  await page.getByRole("button", { name: "Refresh readiness now" }).click();
  await expect(page.getByRole("button", { name: "Run Llama completion" })).toBeDisabled();
  await expect(models).toBeDisabled();
  await expect(models).toHaveValue("gemma-sleeping");

  await request.post("/proxy/llama/llama-mode?mode=fallback");
  await page.getByRole("button", { name: "Refresh readiness now" }).click();
  await expect(models).toHaveValue("gemma-fallback");
});

test("Llama preparation failure disables Run and Retry restores the prepared selection", async ({ page, request }) => {
  await request.post("/proxy/llama/llama-mode?mode=models-failure");
  await page.goto("/detail.html?service=llama");
  await expect(page.getByText("Model preparation failed.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Run Llama completion" })).toBeDisabled();
  await request.post("/proxy/llama/llama-mode?mode=success");
  await page.getByRole("button", { name: "Retry model preparation" }).click();
  await expect(page.getByRole("combobox", { name: "Model preset" })).toHaveValue("gemma-loaded");
  await expect(page.getByRole("button", { name: "Run Llama completion" })).toBeEnabled();
});

test("Llama cancellation and incomplete streams settle once without late result mutation", async ({ page, request }) => {
  await request.post("/proxy/llama/llama-mode?mode=slow");
  await openPrepared(page);
  await page.getByLabel("Prompt").fill("wait");
  await page.getByRole("button", { name: "Run Llama completion" }).click();
  await expect(page.getByTestId("live-thinking")).toContainText("partial thought");
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(0);

  await request.post("/proxy/llama/llama-mode?mode=incomplete");
  await page.getByLabel("Prompt").fill("incomplete");
  await page.getByRole("button", { name: "Run Llama completion" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("The service response ended before completion.");
  await expect(page.locator("[data-run-entry]").first()).not.toContainText("streamed answer");

});

test("Llama target disconnect settles through the real proxy without fabricating a result", async ({ page, request }) => {
  await request.post("/proxy/llama/llama-mode?mode=disconnect");
  await openPrepared(page);
  await page.getByLabel("Prompt").fill("disconnect");
  await page.getByRole("button", { name: "Run Llama completion" }).click();
  await expect(page.locator("[data-run-entry]")).toHaveCount(1);
  await expect(page.locator("[data-run-entry]").first()).toContainText("Unavailable");
  await expect(page.locator("[data-run-entry]").first()).not.toContainText("Thinking");
});
