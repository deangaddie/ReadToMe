import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

for (const scenario of [
  { name: "overview light desktop", path: "/", theme: "light", width: 1440, mixed: false },
  { name: "overview dark narrow", path: "/", theme: "dark", width: 390, mixed: false },
  { name: "overview attention and neutral states", path: "/", theme: "light", width: 1440, mixed: true },
  { name: "valid detail", path: "/detail.html?service=llama", theme: "light", width: 1440, mixed: false },
  { name: "similarity detail idle", path: "/detail.html?service=minilm-l6", theme: "light", width: 1440, mixed: false, run: "idle" },
  { name: "similarity running", path: "/detail.html?service=minilm-l6", theme: "dark", width: 1440, mixed: false, run: "running" },
  { name: "similarity success history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "light", width: 390, mixed: false, run: "success" },
  { name: "similarity failure history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "dark", width: 1440, mixed: false, run: "failure" },
  { name: "similarity cancelled history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "light", width: 390, mixed: false, run: "cancelled" },
  { name: "invalid detail", path: "/detail.html?service=invalid", theme: "dark", width: 390, mixed: false }
] as const) {
  test(`${scenario.name} has no WCAG 2.2 A/AA violations`, async ({ page }) => {
    const browserErrors: string[] = [];
    page.on("pageerror", (error) => browserErrors.push(error.message));
    page.on("console", (message) => { if (message.type() === "error") browserErrors.push(message.text()); });
    await page.setViewportSize({ width: scenario.width, height: 900 });
    await page.addInitScript((theme) => localStorage.setItem("chd.theme", theme), scenario.theme);
    await page.goto(scenario.path);
    if (scenario.path === "/") {
      await expect(page.locator('[data-service-card] [data-state="Ready"]')).toHaveCount(9);
      if (scenario.mixed) {
        await page.evaluate(() => {
          const assignments = [
            ["llama", "Error", "attention", "!"],
            ["chatterbox", "Unknown", "attention", "?"],
            ["voxcpm2", "Loading", "inactive", "↻"],
            ["whisper", "Unavailable", "inactive", "○"]
          ] as const;
          for (const [id, state, group, icon] of assignments) {
            const card = document.querySelector<HTMLElement>(`[data-service-card="${id}"]`)!;
            const badge = card.querySelector<HTMLElement>("[data-state]")!;
            badge.dataset.state = state;
            badge.firstElementChild!.textContent = icon;
            card.querySelector<HTMLElement>("[data-state-text]")!.textContent = state;
            document.querySelector<HTMLElement>(`[data-group="${group}"]`)!.append(card);
          }
          document.querySelector<HTMLElement>('[data-service-card="voxcpm2"] [data-checking]')!.hidden = false;
        });
      }
    }
    if ("run" in scenario && scenario.run !== "idle") {
      await page.getByLabel("First text").fill(scenario.run === "failure" ? "malformed" : scenario.run === "success" ? "same" : "slow");
      await page.getByLabel("Second text").fill("fixture");
      await page.getByRole("button", { name: "Run similarity test" }).click();
      if (scenario.run === "running") await expect(page.getByRole("button", { name: "Cancel run" })).toBeVisible();
      if (scenario.run === "success") await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
      if (scenario.run === "failure") await expect(page.locator('[data-run-entry][data-outcome="failed"]')).toBeVisible();
      if (scenario.run === "cancelled") {
        await page.getByRole("button", { name: "Cancel run" }).click();
        await expect(page.locator('[data-run-entry][data-outcome="cancelled"]')).toBeVisible();
      }
    }
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(browserErrors).toEqual([]);
    expect(results.violations).toEqual([]);
  });
}
