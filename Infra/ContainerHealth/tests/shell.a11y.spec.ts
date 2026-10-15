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
  { name: "audio.cpp clone form idle", path: "/detail.html?service=audiocpp", theme: "light", width: 1440, mixed: false, tts: "idle" },
  { name: "audio.cpp validation and warning states narrow", path: "/detail.html?service=audiocpp", theme: "dark", width: 390, mixed: false, tts: "invalid" },
  { name: "audio.cpp voice design audio result and diagnostic", path: "/detail.html?service=audiocpp", theme: "light", width: 1440, mixed: false, tts: "success", model: "qwen3-design" },
  { name: "audio.cpp clone audio history narrow", path: "/detail.html?service=audiocpp", theme: "dark", width: 390, mixed: false, tts: "success", model: "breeze-q8" },
  { name: "audio.cpp model switch in progress", path: "/detail.html?service=audiocpp", theme: "dark", width: 1440, mixed: false, tts: "running", model: "voxcpm2" },
  { name: "audio.cpp busy outcome", path: "/detail.html?service=audiocpp", theme: "light", width: 1440, mixed: false, tts: "busy", model: "chatterbox" },
  { name: "audio.cpp protocol failure detail", path: "/detail.html?service=audiocpp", theme: "dark", width: 1440, mixed: false, tts: "failure", model: "chatterbox" },
  { name: "Whisper transcription form and Advanced groups idle", path: "/detail.html?service=whisper", theme: "light", width: 1440, mixed: false, whisper: "idle" },
  { name: "Whisper validation and warning states narrow", path: "/detail.html?service=whisper", theme: "dark", width: 390, mixed: false, whisper: "invalid" },
  { name: "Whisper transcription in progress", path: "/detail.html?service=whisper", theme: "dark", width: 1440, mixed: false, whisper: "running" },
  { name: "Whisper transcript, words, and segment metadata", path: "/detail.html?service=whisper", theme: "light", width: 1440, mixed: false, whisper: "success" },
  { name: "Whisper transcript result narrow", path: "/detail.html?service=whisper", theme: "dark", width: 390, mixed: false, whisper: "success" },
  { name: "Whisper verbatim subtitle result", path: "/detail.html?service=whisper", theme: "light", width: 1440, mixed: false, whisper: "srt" },
  { name: "Whisper protocol failure detail", path: "/detail.html?service=whisper", theme: "dark", width: 1440, mixed: false, whisper: "failure" },
  { name: "Whisper cancelled run detail", path: "/detail.html?service=whisper", theme: "light", width: 1440, mixed: false, whisper: "cancelled" },
  { name: "invalid detail", path: "/detail.html?service=invalid", theme: "dark", width: 390, mixed: false }
] as const) {
  test(`${scenario.name} has no WCAG 2.2 A/AA violations`, async ({ page, request }) => {
    const browserErrors: string[] = [];
    page.on("pageerror", (error) => browserErrors.push(error.message));
    // The busy scenario's 503 is the response under test; Chromium still logs every failed load.
    const expectedLoadFailure = "tts" in scenario && scenario.tts === "busy" ? "status of 503" : undefined;
    page.on("console", (message) => {
      if (message.type() === "error" && (expectedLoadFailure === undefined || !message.text().includes(expectedLoadFailure))) browserErrors.push(message.text());
    });
    await page.setViewportSize({ width: scenario.width, height: 900 });
    await page.addInitScript((theme) => localStorage.setItem("chd.theme", theme), scenario.theme);
    if ("llamaRun" in scenario) await request.post(`/proxy/llama/llama-mode?mode=${scenario.llamaRun === "running" ? "slow" : scenario.llamaRun === "failure" ? "incomplete" : "success"}`);
    await page.goto(scenario.path);
    if (scenario.path === "/") {
      await expect(page.locator('[data-service-card] [data-state="Ready"]')).toHaveCount(5);
      if (scenario.mixed) {
        await page.evaluate(() => {
          const assignments = [
            ["llama", "Error", "attention", "!"],
            ["audiocpp", "Unknown", "attention", "?"],
            ["minilm-l6", "Loading", "inactive", "↻"],
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
          document.querySelector<HTMLElement>('[data-service-card="minilm-l6"] [data-checking]')!.hidden = false;
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
    if ("tts" in scenario) {
      await request.post("/proxy/llama/tts-events?reset=1&loaded=breeze-q8");
      await expect(page.getByText("6 models prepared.")).toBeVisible();
      await page.getByText("Advanced", { exact: true }).click();
      if (scenario.tts === "invalid") {
        await page.getByRole("combobox", { name: "Model" }).selectOption("qwen3-base");
        await page.getByRole("button", { name: "Generate speech" }).click();
        await expect(page.getByText("Enter text to speak.")).toBeVisible();
        await page.setInputFiles("#field-reference_audio", { name: "reference.flac", mimeType: "audio/flac", buffer: REFERENCE_BYTES });
        await expect(page.getByText(/WAV and MP3 are the documented inputs/u)).toBeVisible();
      } else if (scenario.tts !== "idle" && "model" in scenario) {
        await page.getByRole("combobox", { name: "Model" }).selectOption(scenario.model);
        const text = { success: "Accessible generated speech", running: "fixture-slow", busy: "fixture-busy", failure: "fixture-malformed-wav" }[scenario.tts];
        await page.getByLabel("Text to speak").fill(text);
        if (await page.locator("#field-reference_audio").isVisible()) await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
        if (await page.getByLabel("Reference transcript").isVisible()) await page.getByLabel("Reference transcript").fill("the exact spoken words");
        if (await page.getByLabel("Design prompt").isVisible()) await page.getByLabel("Design prompt").fill("A calm narrator");
        await page.getByRole("button", { name: "Generate speech" }).click();
        if (scenario.tts === "running") {
          await expect(page.locator("[data-run-progress]")).toContainText("Switching audio.cpp to voxcpm2");
        } else {
          const outcome = { success: "succeeded", busy: "busy", failure: "failed" }[scenario.tts];
          await expect(page.locator(`[data-run-entry][data-outcome="${outcome}"]`)).toBeVisible();
          await page.locator("[data-run-entry] details").evaluateAll((nodes) => { for (const node of nodes) (node as HTMLDetailsElement).open = true; });
          await expect(page.locator("[data-run-entry] pre").first()).toBeVisible();
        }
      }
    }
    if ("whisper" in scenario) {
      await page.locator("[data-advanced-group]").evaluateAll((nodes) => { for (const node of nodes) (node as HTMLDetailsElement).open = true; });
      if (scenario.whisper === "invalid") {
        await page.getByRole("button", { name: "Transcribe audio" }).click();
        await expect(page.getByText("Choose a WAV audio file.")).toBeVisible();
        await page.setInputFiles("#field-file", { name: "speech.mp3", mimeType: "audio/mpeg", buffer: REFERENCE_BYTES });
        await page.getByLabel("Enable VAD").check();
        await page.getByLabel("Best of").fill("2.5");
        await page.getByRole("button", { name: "Transcribe audio" }).click();
        await expect(page.locator('[data-field-warning="vad"]')).toContainText("no VAD model");
        await expect(page.getByText("Enter a whole number.")).toBeVisible();
      } else if (scenario.whisper !== "idle") {
        const name = scenario.whisper === "failure" ? "fixture-malformed.wav"
          : scenario.whisper === "running" || scenario.whisper === "cancelled" ? "fixture-slow.wav" : "speech.wav";
        if (scenario.whisper === "srt") await page.getByLabel("Response format").selectOption("srt");
        await page.setInputFiles("#field-file", { name, mimeType: "audio/wav", buffer: REFERENCE_BYTES });
        await page.getByRole("button", { name: "Transcribe audio" }).click();
        if (scenario.whisper === "running") {
          await expect(page.getByRole("button", { name: "Cancel run" })).toBeVisible();
        } else {
          if (scenario.whisper === "cancelled") await page.getByRole("button", { name: "Cancel run" }).click();
          await expect(page.locator(`[data-run-entry][data-outcome="${scenario.whisper === "failure" ? "failed" : scenario.whisper === "cancelled" ? "cancelled" : "succeeded"}"]`)).toBeVisible();
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
