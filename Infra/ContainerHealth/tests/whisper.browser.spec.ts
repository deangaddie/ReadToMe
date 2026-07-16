import { expect, test, type Page } from "@playwright/test";
import { buildWav } from "./fixtures/wav-fixture";

/** Canonical WAV: 24 kHz mono PCM16, exactly what the mounted base.en model expects. */
const CANONICAL_BYTES = Buffer.from(buildWav({ samples: 64 }));
const STEREO_BYTES = Buffer.from(buildWav({ samples: 64, channels: 2, sampleRate: 44_100 }));

const ADVANCED_GROUPS = [
  "Slicing and context", "Decoding", "Language and task", "Timing and output", "Speech and speakers", "Voice activity detection"
];

interface WhisperEvents {
  readonly requests: ReadonlyArray<{
    readonly contentType: string;
    readonly fields: Record<string, string>;
    readonly hasPrompt: boolean;
  }>;
}

async function whisperEvents(request: { get: (url: string) => Promise<{ json: () => Promise<unknown> }> }): Promise<WhisperEvents> {
  return await request.get("/proxy/llama/whisper-events").then(async (response) => await response.json() as WhisperEvents);
}

async function chooseAudio(page: Page, name = "speech.wav", buffer: Buffer = CANONICAL_BYTES, mimeType = "audio/wav"): Promise<void> {
  await page.setInputFiles("#field-file", { name, mimeType, buffer });
}

async function openGroups(page: Page): Promise<void> {
  await page.locator("[data-advanced-group]").evaluateAll((nodes) => { for (const node of nodes) (node as HTMLDetailsElement).open = true; });
}

async function transcribe(page: Page, name = "speech.wav", buffer: Buffer = CANONICAL_BYTES): Promise<void> {
  await chooseAudio(page, name, buffer);
  await page.getByRole("button", { name: "Transcribe audio" }).click();
}

test("Whisper exposes every labelled group and transcribes with the word-alignment defaults", async ({ page, request }) => {
  page.on("pageerror", (error) => { throw error; });
  await request.post("/proxy/llama/whisper-events?reset=1");
  await page.goto("/detail.html?service=whisper");
  await expect(page.getByRole("heading", { name: "Whisper.cpp", level: 1 })).toBeVisible();

  // Common fields carry the confirmation defaults and the picker advertises WAV only.
  await expect(page.locator("#field-file")).toHaveAttribute("accept", ".wav,audio/wav");
  await expect(page.getByLabel("Response format")).toHaveValue("verbose_json");
  await expect(page.locator("#field-language")).toHaveValue("en");
  await expect(page.getByLabel("Word timestamps")).toBeChecked();

  // Every Advanced group is its own labelled native disclosure.
  for (const group of ADVANCED_GROUPS) {
    await expect(page.locator(`[data-advanced-group="${group}"] > summary`)).toHaveText(group);
  }
  await expect(page.locator("[data-advanced-group]")).toHaveCount(ADVANCED_GROUPS.length);
  await openGroups(page);
  await expect(page.getByLabel("Maximum segment length")).toHaveValue("1");
  await expect(page.getByLabel("Split on word")).toBeChecked();
  await expect(page.getByLabel("Best of")).toHaveValue("2");
  await expect(page.getByLabel("Beam size")).toHaveValue("-1");
  await expect(page.getByLabel("VAD maximum speech (s)")).toHaveValue("3.402823466e38");
  await expect(page.getByLabel("Enable VAD")).not.toBeChecked();

  // Every prefilled default is the service's own, so no native constraint may reject one.
  const nativelyInvalid = await page.locator("form input[type=number]").evaluateAll(
    (nodes) => nodes.filter((node) => !(node as HTMLInputElement).checkValidity()).map((node) => (node as HTMLInputElement).name)
  );
  expect(nativelyInvalid).toEqual([]);

  await transcribe(page);
  const entry = page.locator('[data-run-entry][data-outcome="succeeded"]');
  await expect(entry).toBeVisible();
  await expect(page.getByTestId("transcript")).toHaveText("It was a bright cold day in April.");
  await expect(page.getByTestId("transcription-meta")).toContainText("Format verbose_json");
  await expect(page.getByTestId("transcription-meta")).toContainText("Language en");
  await expect(page.getByTestId("transcription-meta")).toContainText("Duration 2.40 s");

  // Words are rendered in service order with their own timings and probabilities.
  const words = page.getByTestId("word-timings").locator("li");
  await expect(words).toHaveCount(8);
  await expect(words.first()).toContainText("It");
  await expect(words.first()).toContainText("0.00 s – 0.20 s");
  await expect(words.first()).toContainText("p 0.98");
  await expect(words.last()).toContainText("April.");
  await expect(page.getByTestId("segment-metadata")).toBeVisible();

  const sent = (await whisperEvents(request)).requests;
  expect(sent).toHaveLength(1);
  expect(sent[0]!.contentType).toMatch(/^multipart\/form-data; boundary=/u);
  expect(sent[0]!.fields).toMatchObject({
    file: "speech.wav", response_format: "verbose_json", language: "en",
    token_timestamps: "true", max_len: "1", split_on_word: "true", no_timestamps: "false", vad: "false"
  });
  // A blank optional prompt is omitted rather than sent empty.
  expect(sent[0]!.hasPrompt).toBe(false);
});

test("native and adapter validation block a run before the service is contacted", async ({ page, request }) => {
  await request.post("/proxy/llama/whisper-events?reset=1");
  await page.goto("/detail.html?service=whisper");
  await page.getByRole("button", { name: "Transcribe audio" }).click();
  await expect(page.getByText("Choose a WAV audio file.")).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);

  await chooseAudio(page);
  await openGroups(page);
  await page.getByLabel("Best of").fill("2.5");
  await page.getByRole("button", { name: "Transcribe audio" }).click();
  await expect(page.getByText("Enter a whole number.")).toBeVisible();
  await expect(page.locator("#field-best_of")).toHaveAttribute("aria-invalid", "true");
  expect((await whisperEvents(request)).requests).toHaveLength(0);

  await page.getByLabel("Best of").fill("2");
  await page.getByRole("button", { name: "Transcribe audio" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
});

test("unsupported Compose combinations warn without rewriting the operator's values", async ({ page, request }) => {
  await request.post("/proxy/llama/whisper-events?reset=1");
  await page.goto("/detail.html?service=whisper");
  await chooseAudio(page);
  await openGroups(page);

  await page.locator("#field-language").fill("fr");
  await expect(page.locator('[data-field-warning="language"]')).toContainText("base.en model is English-only");
  await page.getByLabel("Enable VAD").check();
  await expect(page.locator('[data-field-warning="vad"]')).toContainText("supplies no VAD model");
  await page.getByLabel("No timestamps").check();
  await expect(page.locator('[data-field-warning="no_timestamps"]')).toContainText("No timestamps produces no timings");

  // A non-WAV choice warns but is still sent, and every edited value survives to the wire unchanged.
  await chooseAudio(page, "speech.mp3", CANONICAL_BYTES, "audio/mpeg");
  await expect(page.locator('[data-field-warning="file"]')).toContainText("WAV is the only supported input");
  await expect(page.locator("#field-language")).toHaveValue("fr");
  await expect(page.getByLabel("Enable VAD")).toBeChecked();
  await expect(page.getByLabel("No timestamps")).toBeChecked();

  await page.getByRole("button", { name: "Transcribe audio" }).click();
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toBeVisible();
  expect((await whisperEvents(request)).requests.at(-1)!.fields).toMatchObject({ language: "fr", vad: "true", no_timestamps: "true" });
});

test("a recognizable non-Canonical WAV is transcribed with a warning rather than blocked", async ({ page }) => {
  await page.goto("/detail.html?service=whisper");
  await transcribe(page, "stereo.wav", STEREO_BYTES);
  const entry = page.locator('[data-run-entry][data-outcome="succeeded"]');
  await expect(entry).toBeVisible();
  await expect(entry.locator(".run-warnings")).toContainText("Canonical WAV");
  await expect(entry.locator(".run-warnings")).toContainText("44100 Hz");
  await expect(page.getByTestId("transcript")).toBeVisible();
});

test("a valid response with no words succeeds with a warning and no fabricated alignment", async ({ page }) => {
  await page.goto("/detail.html?service=whisper");
  await transcribe(page, "fixture-no-words.wav");
  const entry = page.locator('[data-run-entry][data-outcome="succeeded"]');
  await expect(entry).toBeVisible();
  await expect(entry.locator(".run-warnings")).toContainText("no word timings");
  await expect(page.getByTestId("word-timings")).toHaveCount(0);
  await expect(page.getByTestId("transcript")).toBeVisible();
});

test("text, SRT, and VTT responses are shown verbatim", async ({ page }) => {
  await page.goto("/detail.html?service=whisper");
  const formats = [
    ["text", " It was a bright cold day in April.\n"],
    ["srt", "1\r\n00:00:00,000 --> 00:00:01,100\r\n It was a bright\r\n"],
    ["vtt", "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.100\r\n It was a bright\r\n"]
  ] as const;
  for (const [index, [format, expected]] of formats.entries()) {
    await page.getByLabel("Response format").selectOption(format);
    await transcribe(page);
    // Each run adds one entry, so waiting on the count avoids reading the previous run's result.
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index + 1);
    // The transcript keeps the exact bytes the service sent, CR and all.
    expect(await page.getByTestId("transcript").first().textContent()).toContain(expected);
    await expect(page.getByTestId("transcription-meta").first()).toContainText(`Format ${format}`);
    // Formats without structured timings never invent a word list.
    await expect(page.locator("[data-run-entry]").first().getByTestId("word-timings")).toHaveCount(0);
  }
});

test("HTTP, protocol, and unreachable failures never fabricate a transcript", async ({ page, request }) => {
  await page.goto("/detail.html?service=whisper");
  for (const [name, expected] of [
    ["fixture-http-error.wav", "failed to read the audio file"],
    ["fixture-malformed.wav", "Protocol failure"],
    ["fixture-wrong-media.wav", "Protocol failure"],
    ["fixture-reversed.wav", "reversed timing"]
  ] as const) {
    await transcribe(page, name);
    await expect(page.locator("[data-run-entry]").first()).toContainText(expected);
    await expect(page.locator("[data-run-entry]").first().getByTestId("transcript")).toHaveCount(0);
  }

  await request.post("/proxy/llama/shutdown-service?service=whisper");
  await transcribe(page);
  await expect(page.locator("[data-run-entry]").first()).toContainText("The dashboard could not reach Whisper.cpp through the local proxy.");
  await expect(page.locator("[data-run-entry]").first().getByTestId("transcript")).toHaveCount(0);
  await request.post("/proxy/llama/restart-service?service=whisper");
});

test("a slow transcription cancels immediately and discards the late result", async ({ page, request }) => {
  page.on("pageerror", (error) => { throw error; });
  await request.post("/proxy/llama/whisper-events?reset=1");
  await page.goto("/detail.html?service=whisper");
  await transcribe(page, "fixture-slow.wav");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeFocused();
  await expect(page.getByText(/Elapsed/u)).toBeVisible();
  await page.getByRole("button", { name: "Cancel run" }).click();
  await expect(page.locator("[data-run-entry]").first()).toContainText("Cancelled by you. The service may continue processing after the connection closes.");
  await expect(page.getByTestId("transcript")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Transcribe audio" })).toBeVisible();
  await expect.poll(async () => (await request.get("/proxy/llama/abort-status").then(async (response) => await response.json() as { abortObserved: boolean })).abortObserved).toBe(true);
  await expect(page.locator('[data-run-entry][data-outcome="cancelled"]')).toHaveCount(1);
  await expect(page.getByTestId("transcript")).toHaveCount(0);
});

test("history keeps five newest-first runs and discloses bounded diagnostics", async ({ page }) => {
  page.on("pageerror", (error) => { throw error; });
  await page.goto("/detail.html?service=whisper");
  for (let index = 1; index <= 5; index += 1) {
    await transcribe(page, `run-${index}.wav`);
    await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(index);
  }
  await transcribe(page, "run-6.wav");
  await expect(page.locator("[data-run-entry]").first()).toContainText("run-6.wav");
  await expect(page.locator('[data-run-entry][data-outcome="succeeded"]')).toHaveCount(5);

  const diagnostic = page.locator("[data-run-entry]").first().locator("pre").last();
  await page.locator("[data-run-entry] details").first().evaluate((node) => { (node as HTMLDetailsElement).open = true; });
  await expect(page.locator("[data-run-entry]").first()).toContainText("run-6.wav");
  await expect(diagnostic).toBeTruthy();
});

test("readiness stays independent of an active run and navigation cleans the page up", async ({ page }) => {
  page.on("pageerror", (error) => { throw error; });
  await page.goto("/detail.html?service=whisper");
  await transcribe(page, "fixture-slow.wav");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeVisible();
  // Readiness keeps observing while the run continues, and never disables the run.
  await expect(page.locator("[data-detail-state-text]")).toHaveText("Ready");
  await expect(page.getByRole("button", { name: "Cancel run" })).toBeEnabled();

  await page.goto("/");
  await expect(page.locator('[data-service-card] [data-state="Ready"]').first()).toBeVisible();
  await expect(page.locator("[data-run-entry]")).toHaveCount(0);
});
