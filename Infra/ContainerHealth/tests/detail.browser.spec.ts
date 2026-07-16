import { expect, test, type Page } from "@playwright/test";

function failOnPageErrors(page: Page): void {
  page.on("pageerror", (error) => { throw error; });
}

test.beforeEach(async ({ request }) => {
  await request.post("/proxy/llama/restart-service?service=minilm-l6");
  await request.post("/proxy/llama/restart-service?service=mpnet-base-v2");
});

async function fillAndRun(page: Page, first: string, second: string): Promise<void> {
  await page.getByLabel("First text").fill(first);
  await page.getByLabel("Second text").fill(second);
  await page.getByRole("button", { name: "Run similarity test" }).click();
}

for (const service of ["minilm-l6", "mpnet-base-v2"] as const) {
  test(`${service} is bookmarkable and sends its exact request through the real proxy`, async ({ page }) => {
    failOnPageErrors(page);
    await page.goto(`/detail.html?service=${service}`);
    await expect(page.getByRole("heading", { name: service === "minilm-l6" ? "MiniLM-L6" : "MPNet Base v2" })).toBeVisible();
    await expect(page.getByLabel("First text")).toBeVisible();
    await expect(page.getByLabel("Second text")).toBeVisible();
    await fillAndRun(page, "identical", "identical");
    await expect(page.getByText("Raw cosine similarity")).toBeVisible();
    await expect(page.getByTestId("similarity-score")).toHaveText("0.987654");
    await page.reload();
    await expect(page.getByLabel("First text")).toBeVisible();
  });
}

test("validation stays inline and outside history; scores are raw, negative, and warning-capable", async ({ page }) => {
  await page.goto("/detail.html?service=minilm-l6");
  await page.getByRole("button", { name: "Run similarity test" }).click();
  await expect(page.getByText("Enter the first text.")).toBeVisible();
  await expect(page.getByText("Enter the second text.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);

  await fillAndRun(page, "negative", "value");
  await expect(page.getByTestId("similarity-score")).toHaveText("-0.25");
  await expect(page.getByText("Raw cosine similarity")).toBeVisible();
  await expect(page.locator(".similarity-percent, meter, progress")).toHaveCount(0);

  await fillAndRun(page, "outside", "range");
  await expect(page.getByTestId("similarity-score").first()).toHaveText("1.25");
  await expect(page.getByText("This raw cosine value is outside the theoretical -1 to 1 range.")).toBeVisible();
});

test("HTTP, malformed, unreachable, cancellation, and newest-first five-entry history have no fabricated results", async ({ page, request }) => {
  await page.goto("/detail.html?service=minilm-l6");
  await fillAndRun(page, "http-error", "case");
  await expect(page.getByText("HTTP failure")).toBeVisible();
  await expect(page.locator("[data-run-entry]").first()).toContainText(/HTTP 422.*fixture rejected input/);
  await expect(page.getByText("Raw cosine similarity")).toHaveCount(0);

  await fillAndRun(page, "malformed", "case");
  await expect(page.getByText("Protocol failure")).toBeVisible();

  await fillAndRun(page, "slow", "case");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeFocused();
  await expect(page.getByText(/Elapsed/)).toBeVisible();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you. The service may continue processing after the connection closes.");
  await expect.poll(async () => {
    const result = await request.get("/proxy/llama/abort-status").then((response) => response.json()) as { abortObserved: boolean };
    return result.abortObserved;
  }).toBeTruthy();

  for (let index = 0; index < 4; index += 1) {
    await fillAndRun(page, `run-${index}`, "case");
    await expect(page.locator("[data-run-entry]").first()).toContainText("Succeeded");
  }
  await expect(page.locator("[data-run-entry]")).toHaveCount(5);

  await request.post("/proxy/llama/shutdown-service?service=minilm-l6");
  await fillAndRun(page, "unreachable", "case");
  await expect(page.locator("[data-run-entry]").first()).toContainText("Unavailable");
  await expect(page.locator("[data-run-entry]").first()).not.toContainText("Raw cosine similarity");
  await request.post("/proxy/llama/restart-service?service=minilm-l6");
});

test("readiness may change while a similarity run continues independently", async ({ page, request }) => {
  await page.goto("/detail.html?service=minilm-l6");
  await expect(page.locator("[data-detail-state-text]")).toHaveText("Ready");
  await fillAndRun(page, "slow", "case");
  await request.post("/proxy/llama/readiness-state?service=minilm-l6&state=error");
  await page.getByRole("button", { name: "Refresh readiness now" }).click();
  await request.post("/proxy/llama/readiness-release");
  await expect(page.locator("[data-detail-state-text]")).toHaveText("Unavailable");
  await expect(page.getByText("Latest readiness changed; the active test is still running.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeVisible();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await request.post("/proxy/llama/readiness-state?service=minilm-l6&state=ready");
});
