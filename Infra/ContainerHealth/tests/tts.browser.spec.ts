import { expect, test, type Page } from "@playwright/test";
import { buildWav } from "./fixtures/wav-fixture";

const FIXTURE_WAV_BYTES = 44 + 24_000;
const REFERENCE_BYTES = Buffer.from(buildWav({ samples: 32 }));

const SERVICES = [
  { id: "chatterbox", name: "Chatterbox TTS", route: "/tts", clone: true, advanced: ["Exaggeration", "CFG weight", "Temperature", "Min P", "Top P", "Repetition penalty"] },
  { id: "chatterbox-turbo", name: "Chatterbox Turbo", route: "/tts/turbo", clone: true, advanced: ["Temperature", "Repetition penalty"] },
  { id: "qwen3-tts", name: "Qwen3 Voice Design", route: "/tts", clone: false, advanced: ["Temperature", "Top P", "Top K", "Repetition penalty", "Max new tokens"] },
  { id: "qwen3-tts-base", name: "Qwen3 TTS Base", route: "/tts", clone: true, advanced: ["Temperature", "Top P", "Top K", "Repetition penalty", "Max new tokens"] }
] as const;

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

async function fillSpeechForm(page: Page, service: (typeof SERVICES)[number], text: string): Promise<void> {
  await page.getByLabel("Text to speak").fill(text);
  if (service.clone) await page.setInputFiles("#field-reference_audio", { name: "reference.wav", mimeType: "audio/wav", buffer: REFERENCE_BYTES });
  if (service.id === "qwen3-tts") await page.getByLabel("Voice description").fill("A calm narrator with a warm delivery");
  if (service.id === "qwen3-tts-base") await page.getByLabel("Reference transcript").fill("the exact spoken words");
}

async function generate(page: Page, service: (typeof SERVICES)[number], text: string): Promise<void> {
  await fillSpeechForm(page, service, text);
  await page.getByRole("button", { name: "Generate speech" }).click();
}

interface TtsEvent { readonly service: string; readonly contentType: string; readonly body: string }

for (const service of SERVICES) {
  test(`${service.id} exposes its full form and generates playable, downloadable WAV through the real proxy`, async ({ page, request }) => {
    page.on("pageerror", (error) => { throw error; });
    await observeResources(page);
    await request.post("/proxy/llama/tts-events?reset=1");
    await page.goto(`/detail.html?service=${service.id}`);
    await expect(page.getByRole("heading", { name: service.name, level: 1 })).toBeVisible();
    await expect(page.getByLabel("Text to speak")).toBeVisible();
    if (service.clone) await expect(page.locator("#field-reference_audio")).toHaveAttribute("accept", /\.wav/u);
    await page.getByText("Advanced", { exact: true }).click();
    for (const label of service.advanced) await expect(page.getByLabel(label)).toBeVisible();

    await generate(page, service, "Speak this line for the operator.");
    const entry = page.locator('[data-run-entry][data-outcome="succeeded"]');
    await expect(entry).toBeVisible();

    const player = page.locator("[data-audio-player]").first();
    const download = page.locator("[data-audio-download]").first();
    const filename = new RegExp(`^read2me-${service.id}-\\d{8}T\\d{6}Z\\.wav$`, "u");
    await expect(download).toHaveAttribute("download", filename);
    const source = await player.getAttribute("src");
    expect(source).toMatch(/^blob:/u);
    expect(await download.getAttribute("href")).toBe(source);
    expect(await page.evaluate(async (url) => (await (await fetch(url)).arrayBuffer()).byteLength, source!)).toBe(FIXTURE_WAV_BYTES);
    await player.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
    await expect.poll(async () => await player.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);

    const events = await request.get("/proxy/llama/tts-events").then(async (response) => await response.json() as { requests: TtsEvent[] });
    const sent = events.requests.at(-1)!;
    expect(sent.service).toBe(service.id);
    expect(sent.contentType).toMatch(/^multipart\/form-data; boundary=/u);
    const body = Buffer.from(sent.body, "base64");
    expect(body.toString("latin1")).toContain('name="text"');
    expect(body.toString("utf8")).toContain("Speak this line for the operator.");
    if (service.clone) {
      expect(body.toString("latin1")).toContain('name="reference_audio"; filename="reference.wav"');
      expect(body.includes(REFERENCE_BYTES)).toBe(true);
    }
  });
}

test("blank Qwen sampling stays omitted on the wire while set values are sent", async ({ page, request }) => {
  await request.post("/proxy/llama/tts-events?reset=1");
  await page.goto("/detail.html?service=qwen3-tts");
  await generate(page, SERVICES[2], "Design a voice");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
  let events = await request.get("/proxy/llama/tts-events").then(async (response) => await response.json() as { requests: TtsEvent[] });
  let body = Buffer.from(events.requests.at(-1)!.body, "base64").toString("latin1");
  expect(body).toContain('name="language"');
  for (const omitted of ["temperature", "top_p", "top_k", "repetition_penalty", "max_new_tokens"]) expect(body).not.toContain(`name="${omitted}"`);

  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("Top K").fill("40");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(2);
  events = await request.get("/proxy/llama/tts-events").then(async (response) => await response.json() as { requests: TtsEvent[] });
  body = Buffer.from(events.requests.at(-1)!.body, "base64").toString("latin1");
  expect(body).toContain('name="top_k"');
  expect(body).toContain("40");
  expect(body).not.toContain('name="temperature"');
});

test("required cloning inputs block runs inline while permissive formats only warn", async ({ page }) => {
  await page.goto("/detail.html?service=qwen3-tts-base");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.getByText("Enter text to speak.")).toBeVisible();
  await expect(page.getByText("Choose a reference audio file.")).toBeVisible();
  await expect(page.getByText("Enter reference transcript.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);

  await page.setInputFiles("#field-reference_audio", { name: "reference.flac", mimeType: "audio/flac", buffer: REFERENCE_BYTES });
  await expect(page.getByText(/WAV and MP3 are the documented inputs/u)).toBeVisible();
  await page.getByLabel("Text to speak").fill("still runs");
  await page.getByLabel("Reference transcript").fill("words");
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
});

test("out-of-range Chatterbox guidance warns without blocking the run", async ({ page }) => {
  await page.goto("/detail.html?service=chatterbox");
  await fillSpeechForm(page, SERVICES[0], "warned but sent");
  await page.getByText("Advanced", { exact: true }).click();
  await page.getByLabel("Exaggeration").fill("1.4");
  await expect(page.getByText(/documented range is 0 to 1/u)).toBeVisible();
  await page.getByRole("button", { name: "Generate speech" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
});

test("service, media, and WAV failures never fabricate an audio result", async ({ page, request }) => {
  await page.goto("/detail.html?service=chatterbox");
  for (const [marker, expected] of [
    ["fixture-http-error", "HTTP 422"],
    ["fixture-wrong-media", "Protocol failure"],
    ["fixture-malformed-wav", "Protocol failure"],
    ["fixture-truncated-wav", "Protocol failure"]
  ] as const) {
    await generate(page, SERVICES[0], marker);
    await expect(page.locator("[data-run-entry]").first()).toContainText(expected);
    await expect(page.locator("[data-run-entry]").first().locator("[data-audio-player]")).toHaveCount(0);
  }
  await expect(page.locator("[data-run-entry]").first()).toContainText("Bytes:");

  await request.post("/proxy/llama/shutdown-service?service=chatterbox");
  await generate(page, SERVICES[0], "unreachable");
  await expect(page.locator("[data-run-entry]").first()).toContainText("Unavailable");
  await expect(page.locator("[data-run-entry]").first()).toContainText("The dashboard could not reach Chatterbox TTS through the local proxy.");
  await request.post("/proxy/llama/restart-service?service=chatterbox");
});

test("a slow generation shows elapsed progress and cancels immediately without late mutation", async ({ page, request }) => {
  await page.goto("/detail.html?service=chatterbox-turbo");
  await generate(page, SERVICES[1], "fixture-slow [sigh]");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeFocused();
  await expect(page.getByText(/Elapsed/u)).toBeVisible();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you. The service may continue processing after the connection closes.");
  await expect(page.locator("[data-audio-player]")).toHaveCount(0);
  await expect.poll(async () => (await request.get("/proxy/llama/abort-status").then(async (response) => await response.json() as { abortObserved: boolean })).abortObserved).toBe(true);
});

test("only one result plays, a sixth run protects active playback, and every URL is revoked exactly once", async ({ page }) => {
  page.on("pageerror", (error) => { throw error; });
  await observeResources(page);
  await page.goto("/detail.html?service=chatterbox");
  for (let index = 1; index <= 5; index += 1) {
    await generate(page, SERVICES[0], `run ${index}`);
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
  expect(await page.evaluate(() => document.querySelectorAll<HTMLAudioElement>("[data-audio-player]").length)).toBe(5);
  expect(await page.evaluate(() => [...document.querySelectorAll<HTMLAudioElement>("[data-audio-player]")].filter((audio) => !audio.paused).length)).toBe(1);

  const protectedSource = await oldest.getAttribute("src");
  await generate(page, SERVICES[0], "run 6");
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
  expect(revoked).toHaveLength(6);
  expect(new Set(revoked).size).toBe(6);
  expect(new Set(revoked)).toEqual(new Set(created));
  const teardown = log.slice(-10);
  expect(teardown.slice(0, 5)).toEqual(["pause", "pause", "pause", "pause", "pause"]);
  expect(teardown.slice(5).every((entry) => entry.startsWith("revoke:"))).toBe(true);
});

test("a paused result becomes evictable again", async ({ page }) => {
  await observeResources(page);
  await page.goto("/detail.html?service=chatterbox");
  for (let index = 1; index <= 5; index += 1) {
    await generate(page, SERVICES[0], `entry ${index}`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  const oldest = page.locator("[data-audio-player]").last();
  const oldestSource = await oldest.getAttribute("src");
  await oldest.evaluate(async (audio: HTMLAudioElement) => { await audio.play(); });
  await expect.poll(async () => await oldest.evaluate((audio: HTMLAudioElement) => !audio.paused)).toBe(true);
  await oldest.evaluate((audio: HTMLAudioElement) => audio.pause());
  await expect.poll(async () => await oldest.evaluate((audio: HTMLAudioElement) => audio.paused)).toBe(true);

  await generate(page, SERVICES[0], "entry 6");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(5);
  await expect(page.locator(`[data-audio-player][src="${oldestSource}"]`)).toHaveCount(0);
  expect((await resourceLog(page)).filter((entry) => entry === `revoke:${oldestSource}`)).toHaveLength(1);
});
