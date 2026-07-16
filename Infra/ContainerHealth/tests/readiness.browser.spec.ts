import { expect, test, type Page } from "@playwright/test";
import { SERVICE_ADAPTERS } from "../src/readiness";

function failOnBrowserErrors(page: Page): void {
  page.on("pageerror", (error) => { throw error; });
  page.on("console", (message) => {
    if (message.type() === "error") throw new Error(`Browser console error: ${message.text()}`);
  });
}

test.beforeEach(async ({ page, request }) => {
  failOnBrowserErrors(page);
  await request.post("/proxy/llama/restart-service?service=mpnet-base-v2");
});

test("overview renders nine complete cards and commits reversed readiness independently", async ({ page, request }) => {
  await request.post("/proxy/llama/readiness-hold?service=llama");
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Service readiness" })).toBeVisible();
  await expect(page.locator("[data-service-card]")) .toHaveCount(9);
  await expect(page.locator("[data-service-link]")) .toHaveCount(9);
  for (const adapter of SERVICE_ADAPTERS) {
    const card = page.locator(`[data-service-card="${adapter.id}"]`);
    await expect(card).toContainText(adapter.name);
    await expect(card).toContainText(adapter.purpose);
    await expect(card).toContainText(adapter.endpoint);
    await expect(card).toContainText(String(adapter.port));
    await expect(card).toContainText(adapter.compute);
    await expect(card.getByRole("link", { name: new RegExp(`Open ${adapter.name}`) })).toHaveAttribute("href", `/detail.html?service=${adapter.id}`);
  }

  const fast = page.locator('[data-service-card="voxcpm2"]');
  const slow = page.locator('[data-service-card="llama"]');
  await expect(fast.getByText("Ready", { exact: true })).toBeVisible();
  await expect(slow.getByText("Checking…", { exact: true })).toBeVisible();
  await request.post("/proxy/llama/readiness-release");
  await expect(page.locator('[data-service-card] [data-state="Ready"]')).toHaveCount(9);
  const events = await page.request.get("/proxy/llama/readiness-events").then((response) => response.json()) as { starts: string[]; completions: string[] };
  expect(new Set(events.starts).size).toBe(9);
  expect(events.starts[0]).toBe("llama");
  expect(events.completions[0]).toBe("voxcpm2");
  expect(events.completions.at(-1)).toBe("llama");
});

test("manual, interval, and visibility refresh retain the last observation while checking", async ({ page }) => {
  await page.goto("/");
  const llama = page.locator('[data-service-card="llama"]');
  await expect(llama.getByText("Ready", { exact: true })).toBeVisible();
  const before = await page.request.get("/proxy/llama/readiness-events").then((response) => response.json()) as { count: number };

  await page.request.post("/proxy/llama/readiness-hold?service=llama");
  await page.getByRole("button", { name: "Refresh readiness now" }).click();
  await expect(llama.getByText("Checking…", { exact: true })).toBeVisible();
  await expect(llama.getByText("Ready", { exact: true })).toBeVisible();
  await page.request.post("/proxy/llama/readiness-release");
  await expect.poll(async () => {
    const value = await page.request.get("/proxy/llama/readiness-events").then((response) => response.json()) as { count: number };
    return value.count;
  }).toBeGreaterThan(before.count);

  await page.getByLabel("Refresh interval").selectOption("2");
  const afterChange = await page.request.get("/proxy/llama/readiness-events").then((response) => response.json()) as { count: number };
  await expect.poll(async () => {
    const value = await page.request.get("/proxy/llama/readiness-events").then((response) => response.json()) as { count: number };
    return value.count;
  }, { timeout: 4_000 }).toBeGreaterThan(afterChange.count);

  await expect(llama.getByText("Checking…", { exact: true })).toBeHidden();
  await page.request.post("/proxy/llama/readiness-hold?service=llama");
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    document.dispatchEvent(new Event("visibilitychange"));
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(llama.getByText("Checking…", { exact: true })).toBeVisible();
  await page.request.post("/proxy/llama/readiness-release");
});

test("theme preferences persist and valid or invalid detail links keep a safe service shell", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("Theme").selectOption("dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await expect(page.getByLabel("Theme")).toHaveValue("dark");

  await page.goto("/detail.html?service=llama");
  await expect(page.getByRole("heading", { name: "Llama router" })).toBeVisible();
  await expect(page.getByText("This service adapter arrives in a later implementation slice.")).toBeVisible();
  await expect(page.locator("[data-service-link]")).toHaveCount(9);

  await page.goto("/detail.html?service=not-a-service");
  await expect(page.getByRole("heading", { name: "Service not found" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Return to readiness overview" })).toBeVisible();
  await expect(page.locator("[data-service-link]")).toHaveCount(9);
});

test("system theme, keyboard focus, and desktop or narrow layouts remain operator-usable", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1400, height: 900 });
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await expect(page.locator("body")).not.toContainText("Docker");

  await page.emulateMedia({ colorScheme: "dark" });
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  const layout = await page.locator(".dashboard-shell").evaluate((node) => getComputedStyle(node).gridTemplateColumns.split(" ").length);
  expect(layout).toBe(3);
  await expect(page.locator(".activity-rail")).toBeVisible();

  const firstService = page.locator('[data-service-link="llama"]');
  await page.keyboard.press("Tab");
  await expect(firstService).toBeFocused();
  expect(await firstService.evaluate((node) => getComputedStyle(node).outlineStyle)).not.toBe("none");
  expect(await firstService.evaluate((node) => Number.parseFloat(getComputedStyle(node).animationDuration))).toBeLessThanOrEqual(0.00001);

  await page.setViewportSize({ width: 390, height: 900 });
  await expect(page.locator(".activity-rail")).toBeHidden();
  expect(await page.locator(".service-rail").evaluate((node) => getComputedStyle(node).overflowX)).toBe("auto");
  expect(await page.locator(".dashboard-shell").evaluate((node) => getComputedStyle(node).display)).toBe("block");
});
