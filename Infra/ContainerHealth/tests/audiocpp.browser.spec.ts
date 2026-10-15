import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { buildWav } from "./fixtures/wav-fixture";

const FIXTURE_WAV_BYTES = 44 + 24_000;
const REFERENCE_BYTES = Buffer.from(buildWav({ samples: 32 }));

interface ModelCase {
  readonly id: string;
  readonly fields: readonly string[];
  readonly hidden: readonly string[];
}

const MODELS: readonly ModelCase[] = [
  { id: "breeze-q8", fields: ["Reference audio", "Reference transcript", "Instruction"], hidden: ["Design prompt", "Language"] },
  { id: "breeze-design", fields: ["Design prompt"], hidden: ["Reference audio", "Reference transcript", "Instruction", "Language"] },
  { id: "voxcpm2", fields: ["Reference audio", "Instruction"], hidden: ["Design prompt", "Reference transcript", "Language"] },
  { id: "chatterbox", fields: ["Reference audio"], hidden: ["Design prompt", "Reference transcript", "Instruction", "Language"] },
  { id: "qwen3-base", fields: ["Reference audio", "Reference transcript", "Language"], hidden: ["Design prompt", "Instruction"] },
  { id: "qwen3-design", fields: ["Design prompt", "Language"], hidden: ["Reference audio", "Reference transcript", "Instruction"] }
];

interface TtsEvents { readonly requests: ReadonlyArray<{ readonly service: string; readonly body: string }>; readonly loaded: string }

async function reset(request: APIRequestContext, loaded = "breeze-q8"): Promise<void> {
  await request.post(`/proxy/llama/tts-events?reset=1&loaded=${loaded}`);
}

async function events(request: APIRequestContext): Promise<TtsEvents> {
  return await request.get("/proxy/llama/tts-events").then(async (response) => await response.json() as TtsEvents);
}

async function open(page: Page): Promise<void> {
  await page.goto("/detail.html?service=audiocpp");
  await expect(page.getByRole("heading", { name: "audio.cpp TTS", level: 1 })).toBeVisible();
  await expect(page.getByText("6 models prepared.")).toBeVisible();
}

/** Fills whatever the selected model takes, then runs. */
async function generate(page: Page, model: string, text: string): Promise<void> {
  await page.getByRole("combobox", { name: "Model" }).selectOption(model);
  await page.getByLabel("Text to speak").fill(text);
  if (await page.locator("#field-reference_audio").isVisible()) {
    await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
  }
  if (await page.getByLabel("Reference transcript").isVisible()) await page.getByLabel("Reference transcript").fill("the exact spoken words");
  if (await page.getByLabel("Design prompt").isVisible()) await page.getByLabel("Design prompt").fill("A calm narrator with a warm delivery");
  await page.getByRole("button", { name: "Generate speech" }).click();
}

/** Records object-URL ownership and playback pauses in order, surviving navigation. */
async function observeResources(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const log = (entry: string): void => {
      const list = JSON.parse(sessionStorage.getItem("test.log") ?? "[]") as string[];
      list.push(entry);
      sessionStorage.setItem("test.log", JSON.stringify(list));
    };
    const create = URL.createObjectURL.bind(URL);
    const revoke = URL.revokeObjectURL.bind(URL);
    URL.createObjectURL = (object: Blob | MediaSource): string => {
      const url = create(object as Blob);
      log(`create:${url}`);
      return url;
    };
    URL.revokeObjectURL = (url: string): void => { log(`revoke:${url}`); revoke(url); };
    const pause = HTMLMediaElement.prototype.pause;
    HTMLMediaElement.prototype.pause = function pauseWithLog(this: HTMLMediaElement): void {
      log("pause");
      pause.call(this);
    };
  });
}

async function resourceLog(page: Page): Promise<string[]> {
  return await page.evaluate(() => JSON.parse(sessionStorage.getItem("test.log") ?? "[]") as string[]);
}

test.beforeEach(async ({ page, request }) => {
  page.on("pageerror", (error) => { throw error; });
  await reset(request);
});

test("the overview card and detail readiness name the loaded model", async ({ page }) => {
  await page.goto("/");
  await expect(page.locator('[data-service-card="audiocpp"] [data-message]')).toHaveText("Validated response received. Loaded model: breeze-q8.");
  await page.goto("/detail.html?service=audiocpp");
  await expect(page.locator("[data-detail-message]")).toHaveText("Validated response received. Loaded model: breeze-q8.");
});

test("the model select is prepared from audio.cpp, defaults to the loaded model, and shows only that model's fields", async ({ page }) => {
  await open(page);
  const select = page.getByRole("combobox", { name: "Model" });
  await expect(select).toHaveValue("breeze-q8");
  await expect(select.locator("option")).toHaveText([
    "breeze-q8 — Loaded · Breeze paragraph TTS", "breeze-design — Not loaded · Breeze voice design",
    "voxcpm2 — Not loaded · VoxCPM2 paragraph TTS and voice design", "chatterbox — Not loaded · Chatterbox paragraph TTS",
    "qwen3-base — Not loaded · Qwen3 Base paragraph TTS", "qwen3-design — Not loaded · Qwen3 voice design"
  ]);
  for (const model of MODELS) {
    await select.selectOption(model.id);
    for (const label of model.fields) await expect(page.getByLabel(label), `${model.id} shows ${label}`).toBeVisible();
    for (const label of model.hidden) await expect(page.getByLabel(label), `${model.id} hides ${label}`).toBeHidden();
  }
  await page.getByText("Advanced", { exact: true }).click();
  await expect(page.getByLabel("Seed")).toBeVisible();
});

for (const model of MODELS) {
  test(`${model.id} generates playable, downloadable WAV through the real proxy`, async ({ page, request }) => {
    await observeResources(page);
    await open(page);
    await generate(page, model.id, "Speak this line for the operator.");
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();

    const player = page.locator("[data-audio-player]").first();
    const download = page.locator("[data-audio-download]").first();
    await expect(download).toHaveAttribute("download", /^read2me-audiocpp-\d{8}T\d{6}Z\.wav$/u);
    const source = await player.getAttribute("src");
    expect(source).toMatch(/^blob:/u);
    expect(await download.getAttribute("href")).toBe(source);
    expect(await page.evaluate(async (url) => (await (await fetch(url)).arrayBuffer()).byteLength, source!)).toBe(FIXTURE_WAV_BYTES);

    const sent = (await events(request)).requests.at(-1)!;
    expect(sent.service).toBe("audiocpp");
    const body = JSON.parse(sent.body) as { model: string; input: string; voice_ref?: { data: string }; options: Record<string, string> };
    expect(body.model).toBe(model.id);
    expect(body.input).toBe("Speak this line for the operator.");
    expect(body.options.seed).toMatch(/^\d+$/u);
    expect(body.voice_ref?.data === undefined ? undefined : Buffer.from(body.voice_ref.data, "base64")).toEqual(model.fields.includes("Reference audio") ? REFERENCE_BYTES : undefined);
    // After the run the model list is prepared again, so the select reports the newly resident model.
    await expect(page.getByRole("combobox", { name: "Model" }).locator(`option[value="${model.id}"]`)).toHaveText(/ — Loaded · /u);
  });
}

test("a model switch is announced while it runs, and cancelling aborts the request", async ({ page, request }) => {
  await open(page);
  await generate(page, "voxcpm2", "fixture-slow");
  await expect(page.locator("[data-run-progress]")).toContainText("Switching audio.cpp to voxcpm2 and generating; the first request after a switch takes 9–31 s.");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeFocused();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you.");
  await expect.poll(async () => (await request.get("/proxy/llama/abort-status").then(async (response) => await response.json() as { abortObserved: boolean })).abortObserved).toBe(true);
});

test("a busy audio.cpp ends the run as Busy, with no audio and no failure styling", async ({ page }) => {
  await open(page);
  await generate(page, "chatterbox", "fixture-busy");
  const entry = page.locator("[data-run-entry]").first();
  await expect(entry).toHaveAttribute("data-outcome", "busy");
  await expect(entry.getByRole("heading", { name: "Busy" })).toBeVisible();
  await expect(entry).toContainText("audio.cpp is generating with another model; run again when it finishes.");
  await expect(entry.locator("[data-audio-player]")).toHaveCount(0);
  await expect(page.locator("[data-run-progress]")).toHaveText("Busy.");
});

test("required inputs block the run inline for the selected model", async ({ page }) => {
  await open(page);
  await page.getByRole("combobox", { name: "Model" }).selectOption("qwen3-base");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.getByText("Enter text to speak.")).toBeVisible();
  await expect(page.getByText("Choose a reference audio file.")).toBeVisible();
  await expect(page.getByText("Enter reference transcript.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);
  await page.setInputFiles("#field-reference_audio", { name: "reference.flac", mimeType: "audio/flac", buffer: REFERENCE_BYTES });
  await expect(page.getByText(/WAV and MP3 are the documented inputs/u)).toBeVisible();
});

test("service, media, and WAV failures never fabricate an audio result", async ({ page, request }) => {
  await open(page);
  for (const [marker, expected] of [
    ["fixture-http-error", "HTTP 500 — model path does not exist"],
    ["fixture-wrong-media", "Protocol failure"],
    ["fixture-malformed-wav", "Protocol failure"],
    ["fixture-truncated-wav", "Protocol failure"]
  ] as const) {
    const before = await page.locator("[data-run-entry]").count();
    await generate(page, "chatterbox", marker);
    await expect(page.locator("[data-run-entry]")).toHaveCount(before + 1);
    await expect(page.locator("[data-run-entry]").first()).toContainText(expected);
    await expect(page.locator("[data-run-entry]").first().locator("[data-audio-player]")).toHaveCount(0);
  }
  // The select keeps its prepared value until the next run re-prepares it, so this run still goes out.
  await request.post("/proxy/llama/shutdown-service?service=audiocpp");
  try {
    await page.getByLabel("Text to speak").fill("unreachable");
    await page.getByRole("button", { name: "Generate speech" }).click();
    await expect(page.locator("[data-run-entry]")).toHaveCount(5);
    await expect(page.locator("[data-run-entry]").first()).toContainText("The dashboard could not reach audio.cpp TTS through the local proxy.");
    // With audio.cpp down the model list cannot be prepared, so Run stays disabled rather than guessing a shape.
    await expect(page.getByText("Model preparation failed.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Generate speech" })).toBeDisabled();
  } finally {
    await request.post("/proxy/llama/restart-service?service=audiocpp");
  }
});

test("only one result plays, a sixth run protects active playback, and every URL is revoked exactly once", async ({ page }) => {
  await observeResources(page);
  await open(page);
  for (let index = 1; index <= 5; index += 1) {
    await generate(page, "chatterbox", `run ${index}`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  const players = page.locator("[data-audio-player]");
  await expect(players).toHaveCount(5);

  const newest = players.first();
  const oldest = players.last();
  await newest.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
  await expect.poll(async () => await newest.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);
  await oldest.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
  await expect.poll(async () => await oldest.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);
  expect(await page.evaluate(() => [...document.querySelectorAll<HTMLAudioElement>("[data-audio-player]")].filter((audio) => !audio.paused).length)).toBe(1);

  const protectedSource = await oldest.getAttribute("src");
  await generate(page, "chatterbox", "run 6");
  // The count is already five before this run lands, so wait for its own entry first.
  await expect(page.locator("[data-run-entry]").first()).toContainText("run 6");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(5);
  await expect(page.locator(`[data-audio-player][src="${protectedSource}"]`)).toHaveCount(1);
  expect(await page.locator(`[data-audio-player][src="${protectedSource}"]`).evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);

  const afterEviction = await resourceLog(page);
  expect(afterEviction.filter((entry) => entry.startsWith("create:"))).toHaveLength(6);
  const evicted = afterEviction.filter((entry) => entry.startsWith("revoke:"));
  expect(evicted).toHaveLength(1);
  expect(evicted[0]).not.toBe(`revoke:${protectedSource}`);

  await page.goto("/");
  await expect(page.locator('[data-service-card] [data-state="Ready"]').first()).toBeVisible();
  const log = await resourceLog(page);
  const created = log.filter((entry) => entry.startsWith("create:")).map((entry) => entry.slice("create:".length));
  const revoked = log.filter((entry) => entry.startsWith("revoke:")).map((entry) => entry.slice("revoke:".length));
  expect(created).toHaveLength(6);
  expect(new Set(revoked)).toEqual(new Set(created));
  expect(revoked).toHaveLength(6);
  const teardown = log.slice(-10);
  expect(teardown.slice(0, 5)).toEqual(["pause", "pause", "pause", "pause", "pause"]);
  expect(teardown.slice(5).every((entry) => entry.startsWith("revoke:"))).toBe(true);
});

test("a paused result becomes evictable again", async ({ page }) => {
  await observeResources(page);
  await open(page);
  for (let index = 1; index <= 5; index += 1) {
    await generate(page, "chatterbox", `entry ${index}`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  const oldest = page.locator("[data-audio-player]").last();
  const oldestSource = await oldest.getAttribute("src");
  await oldest.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
  await expect.poll(async () => await oldest.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);
  await oldest.evaluate((audio: HTMLAudioElement) => audio.pause());
  await expect.poll(async () => await oldest.evaluate((audio: HTMLAudioElement) => audio.paused)).toBe(true);

  await generate(page, "chatterbox", "entry 6");
  await expect(page.locator("[data-run-entry]").first()).toContainText("entry 6");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(5);
  await expect(page.locator(`[data-audio-player][src="${oldestSource}"]`)).toHaveCount(0);
  expect((await resourceLog(page)).filter((entry) => entry === `revoke:${oldestSource}`)).toHaveLength(1);
});
