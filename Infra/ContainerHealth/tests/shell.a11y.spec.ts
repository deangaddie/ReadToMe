import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { buildWav } from "./fixtures/wav-fixture";

const REFERENCE_BYTES = Buffer.from(buildWav({ samples: 32 }));

for (const scenario of [
  { name: "overview light desktop", path: "/", theme: "light", width: 1440, mixed: false },
  { name: "overview dark narrow", path: "/", theme: "dark", width: 390, mixed: false },
  { name: "overview attention and neutral states", path: "/", theme: "light", width: 1440, mixed: true },
  { name: "Llama prepared detail", path: "/detail.html?service=llama", theme: "light", width: 1440, mixed: false, llamaRun: "prepared" },
  { name: "Llama streamed running detail", path: "/detail.html?service=llama", theme: "dark", width: 1440, mixed: false, llamaRun: "running" },
  { name: "Llama result detail", path: "/detail.html?service=llama", theme: "light", width: 390, mixed: false, llamaRun: "success" },
  { name: "Llama incomplete stream error detail", path: "/detail.html?service=llama", theme: "dark", width: 1440, mixed: false, llamaRun: "failure" },
  { name: "similarity detail idle", path: "/detail.html?service=minilm-l6", theme: "light", width: 1440, mixed: false, run: "idle" },
  { name: "similarity running", path: "/detail.html?service=minilm-l6", theme: "dark", width: 1440, mixed: false, run: "running" },
  { name: "similarity success history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "light", width: 390, mixed: false, run: "success" },
  { name: "similarity failure history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "dark", width: 1440, mixed: false, run: "failure" },
  { name: "similarity cancelled history and diagnostic", path: "/detail.html?service=minilm-l6", theme: "light", width: 390, mixed: false, run: "cancelled" },
  { name: "Chatterbox speech form idle", path: "/detail.html?service=chatterbox", theme: "light", width: 1440, mixed: false, tts: "idle" },
  { name: "Turbo validation and warning states", path: "/detail.html?service=chatterbox-turbo", theme: "dark", width: 390, mixed: false, tts: "invalid" },
  { name: "Qwen Voice Design audio result and diagnostic", path: "/detail.html?service=qwen3-tts", theme: "light", width: 1440, mixed: false, tts: "success" },
  { name: "Qwen Base audio history narrow", path: "/detail.html?service=qwen3-tts-base", theme: "dark", width: 390, mixed: false, tts: "success" },
  { name: "Chatterbox protocol failure detail", path: "/detail.html?service=chatterbox", theme: "dark", width: 1440, mixed: false, tts: "failure" },
  { name: "VoxCPM2 upload form and Advanced groups idle", path: "/detail.html?service=voxcpm2", theme: "light", width: 1440, mixed: false, vox: "idle" },
  { name: "VoxCPM2 validation states narrow", path: "/detail.html?service=voxcpm2", theme: "dark", width: 390, mixed: false, vox: "invalid" },
  { name: "VoxCPM2 streaming run in progress", path: "/detail.html?service=voxcpm2", theme: "dark", width: 1440, mixed: false, vox: "running" },
  { name: "VoxCPM2 assembled audio result and diagnostic", path: "/detail.html?service=voxcpm2", theme: "light", width: 1440, mixed: false, vox: "success" },
  { name: "VoxCPM2 audio result narrow", path: "/detail.html?service=voxcpm2", theme: "dark", width: 390, mixed: false, vox: "success" },
  { name: "VoxCPM2 framed error failure detail", path: "/detail.html?service=voxcpm2", theme: "dark", width: 1440, mixed: false, vox: "failure" },
  { name: "VoxCPM2 cancelled run detail", path: "/detail.html?service=voxcpm2", theme: "light", width: 1440, mixed: false, vox: "cancelled" },
  { name: "invalid detail", path: "/detail.html?service=invalid", theme: "dark", width: 390, mixed: false }
] as const) {
  test(`${scenario.name} has no WCAG 2.2 A/AA violations`, async ({ page, request }) => {
    const browserErrors: string[] = [];
    page.on("pageerror", (error) => browserErrors.push(error.message));
    page.on("console", (message) => { if (message.type() === "error") browserErrors.push(message.text()); });
    await page.setViewportSize({ width: scenario.width, height: 900 });
    await page.addInitScript((theme) => localStorage.setItem("chd.theme", theme), scenario.theme);
    if ("llamaRun" in scenario) await request.post(`/proxy/llama/llama-mode?mode=${scenario.llamaRun === "running" ? "slow" : scenario.llamaRun === "failure" ? "incomplete" : "success"}`);
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
    if ("tts" in scenario && scenario.tts !== "idle") {
      const clone = scenario.path.includes("chatterbox") || scenario.path.includes("qwen3-tts-base");
      if (scenario.tts === "invalid") {
        await page.getByRole("button", { name: "Generate speech" }).click();
        await expect(page.getByText("Enter text to speak.")).toBeVisible();
        await page.setInputFiles("#field-reference_audio", { name: "reference.flac", mimeType: "audio/flac", buffer: REFERENCE_BYTES });
        await expect(page.getByText(/WAV and MP3 are the documented inputs/u)).toBeVisible();
      } else {
        await page.getByLabel("Text to speak").fill(scenario.tts === "failure" ? "fixture-malformed-wav" : "Accessible generated speech");
        if (clone) await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
        if (scenario.path.includes("service=qwen3-tts&") || scenario.path.endsWith("service=qwen3-tts")) await page.getByLabel("Voice description").fill("A calm narrator");
        if (scenario.path.includes("qwen3-tts-base")) await page.getByLabel("Reference transcript").fill("the exact spoken words");
        await page.getByRole("button", { name: "Generate speech" }).click();
        await expect(page.locator(`[data-run-entry][data-outcome="${scenario.tts === "failure" ? "failed" : "succeeded"}"]`)).toBeVisible();
        await page.locator("[data-run-entry] details").evaluateAll((nodes) => { for (const node of nodes) (node as HTMLDetailsElement).open = true; });
        await expect(page.locator("[data-run-entry] pre").first()).toBeVisible();
      }
    }
    if ("vox" in scenario) {
      await page.getByText("Advanced", { exact: true }).click();
      if (scenario.vox === "invalid") {
        await page.getByRole("button", { name: "Generate speech" }).click();
        await expect(page.getByText("Enter text to speak.")).toBeVisible();
        await page.getByLabel("Text to speak").fill("blocked");
        await page.setInputFiles("#field-reference_audio", { name: "reference.aac", mimeType: "audio/aac", buffer: REFERENCE_BYTES });
        await page.getByLabel("Minimum length").fill("5000");
        await page.getByRole("button", { name: "Generate speech" }).click();
        await expect(page.getByText("Choose a .wav, .mp3, .flac, .ogg, .m4a file.")).toBeVisible();
        await expect(page.getByText("The minimum length cannot exceed the maximum length.")).toBeVisible();
      } else if (scenario.vox !== "idle") {
        const marker = scenario.vox === "failure" ? "fixture-framed-error"
          : scenario.vox === "running" || scenario.vox === "cancelled" ? "fixture-slow" : "Accessible streamed speech";
        await page.getByLabel("Text to speak").fill(marker);
        await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
        await page.getByRole("button", { name: "Generate speech" }).click();
        if (scenario.vox === "running") {
          await expect(page.getByRole("button", { name: "Cancel run" })).toBeVisible();
        } else {
          if (scenario.vox === "cancelled") await page.getByRole("button", { name: "Cancel run" }).click();
          await expect(page.locator(`[data-run-entry][data-outcome="${scenario.vox === "success" ? "succeeded" : scenario.vox === "cancelled" ? "cancelled" : "failed"}"]`)).toBeVisible();
          await page.locator("[data-run-entry] details").evaluateAll((nodes) => { for (const node of nodes) (node as HTMLDetailsElement).open = true; });
          await expect(page.locator("[data-run-entry] pre").first()).toBeVisible();
        }
      }
    }
    if ("llamaRun" in scenario) {
      await expect(page.getByRole("combobox", { name: "Model preset" })).toHaveValue("gemma-loaded");
      if (scenario.llamaRun !== "prepared") {
        await page.getByLabel("Prompt").fill("Explain accessible streaming");
        await page.getByRole("button", { name: "Run Llama completion" }).click();
        if (scenario.llamaRun === "running") await expect(page.getByTestId("live-thinking")).toContainText("partial thought");
        if (scenario.llamaRun === "success") await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
        if (scenario.llamaRun === "failure") await expect(page.locator('[data-run-entry][data-outcome="failed"]')).toBeVisible();
      }
    }
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
    expect(browserErrors).toEqual([]);
    expect(results.violations).toEqual([]);
  });
}
