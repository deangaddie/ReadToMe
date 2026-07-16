import { expect, test, type Page } from "@playwright/test";
import { buildWav } from "./fixtures/wav-fixture";

/** The fake target streams three 1200-sample PCM frames, so the assembled mono PCM16 WAV is fixed. */
const EXPECTED_SAMPLES = 3_600;
const EXPECTED_WAV_BYTES = 44 + EXPECTED_SAMPLES * 2;
const REFERENCE_BYTES = Buffer.from(buildWav({ samples: 32 }));

const ADVANCED_FIELDS = [
  "CFG value", "Inference timesteps", "Minimum length", "Maximum length",
  "Normalize text", "Denoise reference", "Retry bad cases", "Retry maximum", "Retry ratio threshold"
];

interface VoxEvents {
  readonly uploads: ReadonlyArray<{ readonly contentType: string; readonly bytes: number; readonly body: string }>;
  readonly requests: ReadonlyArray<Record<string, unknown>>;
}

async function voxEvents(request: { get: (url: string) => Promise<{ json: () => Promise<unknown> }> }): Promise<VoxEvents> {
  return await request.get("/proxy/llama/vox-events").then(async (response) => await response.json() as VoxEvents);
}

async function fillForm(page: Page, text: string): Promise<void> {
  await page.getByLabel("Text to speak").fill(text);
  await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
}

async function generate(page: Page, text: string): Promise<void> {
  await fillForm(page, text);
  await page.getByRole("button", { name: "Generate speech" }).click();
}

async function observeResources(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const log = (entry: string): void => {
      const list = JSON.parse(sessionStorage.getItem("test.log") ?? "[]") as string[];
      list.push(entry);
      sessionStorage.setItem("test.log", JSON.stringify(list));
    };
    const create = URL.createObjectURL.bind(URL);
    const revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = (object: Blob | MediaSource): string => { const url = create(object as Blob); log(`create:${url}`); return url; };
    URL.revokeObjectURL = (url: string): void => { log(`revoke:${url}`); revoke(url); };
  });
}

test("VoxCPM2 exposes every field and streams a playable, downloadable WAV through the real proxy", async ({ page, request }) => {
  page.on("pageerror", (error) => { throw error; });
  await observeResources(page);
  await request.post("/proxy/llama/vox-events?reset=1");
  await page.goto("/detail.html?service=voxcpm2");
  await expect(page.getByRole("heading", { name: "VoxCPM2", level: 1 })).toBeVisible();
  await expect(page.getByLabel("Text to speak")).toBeVisible();
  await expect(page.getByLabel("Control")).toBeVisible();
  await expect(page.locator("#field-reference_audio")).toHaveAttribute("accept", ".wav,.mp3,.flac,.ogg,.m4a");
  await page.getByText("Advanced", { exact: true }).click();
  for (const label of ADVANCED_FIELDS) await expect(page.getByLabel(label)).toBeVisible();
  await expect(page.getByLabel("Retry bad cases")).toBeChecked();
  await expect(page.getByLabel("Normalize text")).not.toBeChecked();

  await fillForm(page, "Speak this line for the operator.");
  await page.getByLabel("Control").fill("whispering");
  await page.getByRole("button", { name: "Generate speech" }).click();

  const entry = page.locator('[data-run-entry][data-outcome="succeeded"]');
  await expect(entry).toBeVisible();
  const player = page.locator("[data-audio-player]").first();
  const download = page.locator("[data-audio-download]").first();
  await expect(download).toHaveAttribute("download", /^read2me-voxcpm2-\d{8}T\d{6}Z\.wav$/u);
  const source = await player.getAttribute("src");
  expect(source).toMatch(/^blob:/u);
  // Download bytes and playback bytes are the same object URL, and the WAV is the assembled stream.
  expect(await download.getAttribute("href")).toBe(source);
  const wav = await page.evaluate(async (url) => [...new Uint8Array(await (await fetch(url)).arrayBuffer())], source!);
  expect(wav).toHaveLength(EXPECTED_WAV_BYTES);
  expect(String.fromCharCode(...wav.slice(0, 4))).toBe("RIFF");
  expect(new DataView(new Uint8Array(wav).buffer).getUint32(24, true)).toBe(24_000);
  await expect(entry).toContainText("24000 Hz");
  await player.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
  await expect.poll(async () => await player.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);

  const events = await voxEvents(request);
  expect(events.uploads).toHaveLength(1);
  expect(events.uploads[0]!.contentType).toMatch(/^multipart\/form-data; boundary=/u);
  const upload = Buffer.from(events.uploads[0]!.body, "base64");
  expect(upload.toString("latin1")).toContain('name="file"; filename="reference.wav"');
  expect(upload.includes(REFERENCE_BYTES)).toBe(true);
  expect(events.requests).toHaveLength(1);
  expect(events.requests[0]).toEqual({
    text: "Speak this line for the operator.", control: "whispering", cfg_value: 2, inference_timesteps: 10,
    min_len: 2, max_len: 4_096, normalize: false, denoise: false, retry_badcase: true,
    retry_badcase_max_times: 3, retry_badcase_ratio_threshold: 6, reference_wav_path: "vox-file-1"
  });
});

test("every run uploads the reference afresh and uses the new identifier immediately", async ({ page, request }) => {
  await request.post("/proxy/llama/vox-events?reset=1");
  await page.goto("/detail.html?service=voxcpm2");
  for (let index = 1; index <= 2; index += 1) {
    await generate(page, `run ${index}`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  const events = await voxEvents(request);
  expect(events.uploads).toHaveLength(2);
  expect(events.requests.map((item) => item.reference_wav_path)).toEqual(["vox-file-1", "vox-file-2"]);
});

test("required and cross-field validation block runs inline without contacting the service", async ({ page, request }) => {
  await request.post("/proxy/llama/vox-events?reset=1");
  await page.goto("/detail.html?service=voxcpm2");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.getByText("Enter text to speak.")).toBeVisible();
  await expect(page.getByText("Choose a reference audio file.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);

  await page.setInputFiles("#field-reference_audio", { name: "reference.aac", mimeType: "audio/aac", buffer: REFERENCE_BYTES });
  await page.getByLabel("Text to speak").fill("blocked");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.getByText("Choose a .wav, .mp3, .flac, .ogg, .m4a file.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);

  await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("Minimum length").fill("5000");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.getByText("The minimum length cannot exceed the maximum length.")).toBeVisible();
  await expect(page.locator("#field-min_len")).toHaveAttribute("aria-invalid", "true");
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);
  expect((await voxEvents(request)).requests).toHaveLength(0);

  await page.getByLabel("Minimum length").fill("2");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
});

test("edited Advanced values reach the wire and a cleared optional field is omitted", async ({ page, request }) => {
  await request.post("/proxy/llama/vox-events?reset=1");
  await page.goto("/detail.html?service=voxcpm2");
  await fillForm(page, "edited controls");
  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("CFG value").fill("3.5");
  await page.getByLabel("Inference timesteps").fill("20");
  await page.getByLabel("Normalize text").check();
  await page.getByLabel("Retry bad cases").uncheck();
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();

  const sent = (await voxEvents(request)).requests.at(-1)!;
  expect(sent).toMatchObject({ cfg_value: 3.5, inference_timesteps: 20, normalize: true, retry_badcase: false, denoise: false });
  // The optional control was never filled, so it is omitted rather than sent blank.
  expect(Object.hasOwn(sent, "control")).toBe(false);
});

test("the run reports upload, generate, and convert phases in order", async ({ page }) => {
  await page.goto("/detail.html?service=voxcpm2");
  const phases: string[] = [];
  const progress = page.locator("[data-run-progress]");
  await fillForm(page, "phase reporting");
  // Sample the live region while the run proceeds rather than waiting on fixed delays.
  const collect = (async (): Promise<void> => {
    for (let index = 0; index < 400; index += 1) {
      const text = await progress.textContent().catch(() => null);
      if (text !== null && !phases.includes(text)) phases.push(text);
      if (text?.includes("Cancelled") === true) return;
      if (await page.locator('[data-run-entry][data-outcome="succeeded"]').count() > 0) return;
    }
  })();
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
  await collect;
  const joined = phases.join(" | ");
  expect(joined).toContain("Uploading the reference audio.");
  expect(joined).toContain("audio");
});

test("framed, reached, media, and unreachable failures never fabricate audio", async ({ page, request }) => {
  await page.goto("/detail.html?service=voxcpm2");
  for (const [marker, expected] of [
    ["fixture-http-error", "HTTP 422"],
    ["fixture-wrong-media", "Protocol failure"],
    ["fixture-protocol", "The service response ended before completion."],
    ["fixture-framed-error", "model not loaded"]
  ] as const) {
    await generate(page, marker);
    await expect(page.locator("[data-run-entry]").first()).toContainText(expected);
    await expect(page.locator("[data-run-entry]").first().locator("[data-audio-player]")).toHaveCount(0);
  }
  // Bounded diagnostics of the truncated stream disclose framing, never PCM content.
  await generate(page, "fixture-protocol");
  const diagnostic = page.locator("[data-run-entry]").first().locator("pre").last();
  await expect(diagnostic).toContainText("frame 1: control meta");
  await expect(diagnostic).toContainText("frame 2: pcm · 64 bytes");
  await expect(diagnostic).toContainText("[incomplete]");
  await expect(diagnostic).not.toContainText("RIFF");

  // The upload carries only the file, so its rejection is driven by the uploaded filename.
  await page.getByLabel("Text to speak").fill("upload rejected");
  await page.setInputFiles("#field-reference_audio", { name: "fixture-upload-error.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("HTTP 400");
  await expect(page.locator("[data-run-entry]").first()).toContainText("unsupported audio format");
  await expect(page.locator("[data-run-entry]").first().locator("[data-audio-player]")).toHaveCount(0);

  await request.post("/proxy/llama/shutdown-service?service=voxcpm2");
  await generate(page, "unreachable");
  await expect(page.locator("[data-run-entry]").first()).toContainText("Unavailable");
  await expect(page.locator("[data-run-entry]").first()).toContainText("The dashboard could not reach VoxCPM2 through the local proxy.");
  await request.post("/proxy/llama/restart-service?service=voxcpm2");
});

test("a slow stream cancels immediately, keeps no audio, and never mutates the page later", async ({ page, request }) => {
  page.on("pageerror", (error) => { throw error; });
  await request.post("/proxy/llama/vox-events?reset=1");
  await page.goto("/detail.html?service=voxcpm2");
  await generate(page, "fixture-slow");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeFocused();
  await expect(page.getByText(/Elapsed/u)).toBeVisible();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you. The service may continue processing after the connection closes.");
  await expect(page.locator("[data-audio-player]")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Generate speech" })).toBeVisible();
  await expect.poll(async () => (await request.get("/proxy/llama/abort-status").then(async (response) => await response.json() as { abortObserved: boolean })).abortObserved).toBe(true);
  // The cancelled run stays cancelled and no late frame adds a result.
  await expect(page.locator('[data-run-entry][data-outcome="cancelled"]')).toHaveCount(1);
  await expect(page.locator("[data-audio-player]")).toHaveCount(0);
});

test("history retains five entries and revokes every evicted URL exactly once", async ({ page }) => {
  page.on("pageerror", (error) => { throw error; });
  await observeResources(page);
  await page.goto("/detail.html?service=voxcpm2");
  for (let index = 1; index <= 5; index += 1) {
    await generate(page, `run ${index}`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  // The sixth run cannot be awaited by entry count, which is already five, so wait for its own entry.
  await generate(page, "run 6");
  await expect(page.locator("[data-run-entry]").first()).toContainText("run 6");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(5);
  await expect(page.locator("[data-audio-player]")).toHaveCount(5);

  await page.goto("/");
  await expect(page.locator('[data-service-card] [data-state="Ready"]').first()).toBeVisible();
  const log = await page.evaluate(() => JSON.parse(sessionStorage.getItem("test.log") ?? "[]") as string[]);
  const created = log.filter((entry) => entry.startsWith("create:")).map((entry) => entry.slice("create:".length));
  const revoked = log.filter((entry) => entry.startsWith("revoke:")).map((entry) => entry.slice("revoke:".length));
  expect(created).toHaveLength(6);
  expect(new Set(revoked)).toEqual(new Set(created));
  expect(revoked).toHaveLength(6);
});
