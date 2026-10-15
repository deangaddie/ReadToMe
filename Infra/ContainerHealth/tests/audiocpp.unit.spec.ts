import { expect, test } from "@playwright/test";
import { DetailRunController } from "../src/run-controller";
import {
  AUDIOCPP_MODELS,
  AUDIOCPP_REFERENCE_LIMIT_BYTES,
  createAudioCppAdapter,
  parseAudioCppModels,
  ServiceFailure,
  type FormValues,
  type ProgressEvent
} from "../src/service-adapter";
import { buildWav } from "./fixtures/wav-fixture";

const adapter = createAudioCppAdapter(() => 0.5);

function reference(name = "voice.wav", type = "audio/wav", bytes: Uint8Array = buildWav()): File {
  return new File([bytes], name, { type });
}

function values(overrides: Record<string, string | File | null> = {}): FormValues {
  return Object.freeze({ ...adapter.initialValues(), ...overrides }) as FormValues;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function modelList(loaded: string | undefined): Response {
  return json({ object: "list", data: AUDIOCPP_MODELS.map(({ id, family, task }) => ({ id, family, task, loaded: id === loaded })) });
}

function wavResponse(): Response {
  return new Response(buildWav({ samples: 12 }), { status: 200, headers: { "content-type": "audio/wav" } });
}

interface Captured { readonly body: Record<string, unknown>; readonly progress: ProgressEvent[] }

/** Runs one generation against a fake audio.cpp: the loaded-model check, then the speech request. */
async function run(formValues: FormValues, options: { loaded?: string; speech?: () => Response } = {}): Promise<Captured & { execution: Awaited<ReturnType<typeof adapter.execute>> }> {
  let body: Record<string, unknown> | undefined;
  const progress: ProgressEvent[] = [];
  const execution = await adapter.execute(formValues, new AbortController().signal, (event) => progress.push(event), async (input, init) => {
    if (String(input) === "/proxy/audiocpp/v1/models") return modelList(options.loaded);
    expect(String(input)).toBe("/proxy/audiocpp/v1/audio/speech");
    expect(init?.method).toBe("POST");
    body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    return (options.speech ?? wavResponse)();
  });
  if (body === undefined) throw new Error("No speech request captured.");
  return { body, progress, execution };
}

async function failure(formValues: FormValues, speech: () => Response): Promise<ServiceFailure> {
  const error = await run(formValues, { loaded: "chatterbox", speech }).then(() => undefined, (caught: unknown) => caught);
  if (!(error instanceof ServiceFailure)) throw new Error(`Expected a ServiceFailure, got ${String(error)}`);
  return error;
}

const REFERENCE_BASE64 = Buffer.from(buildWav()).toString("base64");

test("one model select drives which fields each model takes", () => {
  expect(adapter.id).toBe("audiocpp");
  expect(adapter.preparationNoun).toBe("models");
  expect(adapter.fields.map(({ key, group, models }) => [key, group, models ?? "all"])).toEqual([
    ["model", "common", "all"],
    ["text", "common", "all"],
    ["design_prompt", "common", ["breeze-design", "qwen3-design"]],
    ["instruction", "common", ["breeze-q8", "voxcpm2"]],
    ["reference_audio", "common", ["breeze-q8", "chatterbox", "qwen3-base", "voxcpm2"]],
    ["reference_text", "common", ["breeze-q8", "qwen3-base"]],
    ["language", "common", ["qwen3-base", "qwen3-design"]],
    ["seed", "advanced", "all"]
  ]);
  expect(AUDIOCPP_MODELS.map(({ id }) => id)).toEqual(["breeze-q8", "breeze-design", "voxcpm2", "chatterbox", "qwen3-base", "qwen3-design"]);
  expect(adapter.fields.find(({ key }) => key === "language")?.options?.map(({ value }) => value))
    .toEqual(["auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"]);
});

test("the live model list is cross-checked against the fixed table", () => {
  const options = parseAudioCppModels({ object: "list", data: [
    { id: "breeze-q8", family: "breeze_tts", task: "clon", loaded: true },
    { id: "voxcpm2", family: "voxcpm2", task: "tts", loaded: false },
    { id: "chatterbox", family: "qwen3_tts", task: "tts", loaded: false },
    { id: "kokoro", family: "kokoro", task: "tts", loaded: false }
  ] });
  expect(options?.map(({ id, state, runnable }) => [id, state, runnable])).toEqual([
    ["breeze-q8", "loaded", true],
    ["breeze-design", "unconfigured", false],
    ["voxcpm2", "unloaded", true],
    ["chatterbox", "unknown", false],
    ["qwen3-base", "unconfigured", false],
    ["qwen3-design", "unconfigured", false],
    ["kokoro", "unknown", false]
  ]);
  expect(options?.[0]?.label).toBe("breeze-q8 — Loaded · Breeze paragraph TTS");
  expect(options?.[1]?.label).toBe("breeze-design — Not configured");
  expect(options?.[3]?.label).toBe("chatterbox — Unexpected qwen3_tts/tts");
  for (const malformed of [{}, { object: "list", data: [{ id: "breeze-q8" }] }, { object: "list", data: [{ id: "", loaded: true }] }]) {
    expect(parseAudioCppModels(malformed)).toBeUndefined();
  }
});

test("validation follows the selected model and ignores fields it does not take", () => {
  expect(adapter.validate(values()).errors).toMatchObject({ model: "Choose a prepared model.", text: "Enter text to speak." });
  const errorsFor = (overrides: Record<string, string>) => Object.fromEntries(Object.entries(adapter.validate(values(overrides)).errors).filter(([, message]) => message !== undefined));
  expect(errorsFor({ model: "breeze-q8" })).toEqual({ text: "Enter text to speak.", reference_audio: "Choose a reference audio file.", reference_text: "Enter reference transcript." });
  expect(errorsFor({ model: "breeze-design", text: "hi" })).toEqual({ design_prompt: "Enter design prompt." });
  expect(errorsFor({ model: "chatterbox", text: "hi" })).toEqual({ reference_audio: "Choose a reference audio file." });
  expect(Object.values(adapter.validate(values({ model: "voxcpm2", text: "hi" })).errors).filter(Boolean)).toEqual([]);
  expect(Object.values(adapter.validate(values({ model: "qwen3-design", text: "hi", design_prompt: "calm" })).errors).filter(Boolean)).toEqual([]);

  const oversized = reference("big.wav", "audio/wav", new Uint8Array(AUDIOCPP_REFERENCE_LIMIT_BYTES + 1));
  expect(adapter.validate(values({ model: "voxcpm2", text: "hi", reference_audio: oversized })).errors.reference_audio).toBe("Choose a reference audio file of 5 MiB or less.");
  const flac = adapter.validate(values({ model: "chatterbox", text: "hi", reference_audio: reference("voice.flac", "audio/flac") }));
  expect(flac.errors.reference_audio).toBeUndefined();
  expect(flac.warnings.reference_audio).toMatch(/WAV and MP3 are the documented inputs/u);

  expect(adapter.validate(values({ model: "voxcpm2", text: "hi", seed: "1.5" })).errors.seed).toBe("Enter a whole number.");
  expect(adapter.validate(values({ model: "voxcpm2", text: "hi", seed: "2147483648" })).errors.seed).toBe("Enter a seed from 0 to 2147483647.");
  expect(adapter.validate(values({ model: "voxcpm2", text: "hi", seed: "42" })).errors.seed).toBeUndefined();
});

test("each model sends its family's request shape with the app's fixed values", async () => {
  const random = String(Math.floor(0.5 * 2_147_483_647));
  const clone = { reference_audio: reference(), reference_text: "spoken words" };

  expect((await run(values({ model: "breeze-q8", text: "Hello.", ...clone }))).body).toEqual({
    model: "breeze-q8", input: "Hello.", voice_ref: { type: "base64", data: REFERENCE_BASE64 }, reference_text: "spoken words",
    options: { guidance_scale: "1", temperature: "0.9", top_k: "50", top_p: "1", seed: random }
  });
  expect((await run(values({ model: "breeze-q8", text: "Hello.", instruction: " whispering ", ...clone }))).body.options).toEqual({
    guidance_scale: "3", temperature: "0.9", top_k: "50", top_p: "1", instruction: "whispering", seed: random
  });
  expect((await run(values({ model: "breeze-design", text: "Sample.", design_prompt: "A calm narrator", ...clone }))).body).toEqual({
    model: "breeze-design", input: "Sample.", options: { guidance_scale: "3", instruction: "A calm narrator", seed: random }
  });
  const vox = { guidance_scale: "2", num_inference_steps: "10", min_tokens: "2", max_tokens: "4096", retry_badcase: "true", retry_badcase_max_times: "3", retry_badcase_ratio_threshold: "6", seed: "7" };
  expect((await run(values({ model: "voxcpm2", text: "Hello.", instruction: "whispering", seed: "7" }))).body).toEqual({ model: "voxcpm2", input: "(whispering)Hello.", options: vox });
  expect((await run(values({ model: "voxcpm2", text: "Hello.", reference_audio: reference(), seed: "7" }))).body).toEqual({
    model: "voxcpm2", input: "Hello.", voice_ref: { type: "base64", data: REFERENCE_BASE64 }, options: vox
  });
  expect((await run(values({ model: "chatterbox", text: "Hello.", ...clone, instruction: "ignored" }))).body).toEqual({
    model: "chatterbox", input: "Hello.", voice_ref: { type: "base64", data: REFERENCE_BASE64 }, language: "en",
    options: { exaggeration: "0.5", guidance_scale: "0.5", temperature: "0.8", min_p: "0.05", top_p: "1", repetition_penalty: "1.2", seed: random }
  });
  expect((await run(values({ model: "qwen3-base", text: "Hello.", language: "english", ...clone }))).body).toEqual({
    model: "qwen3-base", input: "Hello.", voice_ref: { type: "base64", data: REFERENCE_BASE64 }, reference_text: "spoken words", language: "english",
    options: { seed: random }
  });
  expect((await run(values({ model: "qwen3-design", text: "Sample.", design_prompt: "A bright voice", ...clone }))).body).toEqual({
    model: "qwen3-design", input: "Sample.", language: "auto", options: { instruction: "A bright voice", seed: random }
  });
});

test("a model that is not loaded runs as a switch, a loaded one as a plain request", async () => {
  const form = values({ model: "voxcpm2", text: "Hello." });
  const switched = await run(form, { loaded: "breeze-q8" });
  expect(switched.progress[0]).toEqual({ kind: "phase", phase: "switch", status: "started", message: "Switching audio.cpp to voxcpm2 and generating; the first request after a switch takes 9–31 s." });
  expect(switched.execution.diagnostic).toContain("Loaded check: voxcpm2 not loaded");
  const resident = await run(form, { loaded: "voxcpm2" });
  expect(resident.progress[0]).toMatchObject({ phase: "request", status: "started", message: "Sending the speech request." });
  expect(resident.execution.result).toMatchObject({ kind: "audio", mediaType: "audio/wav", filename: expect.stringMatching(/^read2me-audiocpp-\d{8}T\d{6}Z\.wav$/u) });

  const progress: ProgressEvent[] = [];
  await adapter.execute(form, new AbortController().signal, (event) => progress.push(event), async (input) =>
    String(input).endsWith("/v1/models") ? json({ error: { message: "gone" } }, 500) : wavResponse());
  expect(progress[0]).toMatchObject({ phase: "request" });
});

test("the reference clip never enters the diagnostic", async () => {
  const { execution } = await run(values({ model: "chatterbox", text: "Hello.", reference_audio: reference("clip.wav") }), { loaded: "chatterbox" });
  expect(execution.diagnostic).not.toContain(REFERENCE_BASE64);
  expect(execution.diagnostic).toContain(`[clip.wav · ${buildWav().byteLength} bytes]`);
});

test("503 server_busy is Busy, not a failure, and other errors keep their categories", async () => {
  const form = values({ model: "chatterbox", text: "Hello.", reference_audio: reference() });
  const busy = await failure(form, () => json({ error: { message: "every loaded model is busy; retry later", type: "server_busy" } }, 503));
  expect(busy).toMatchObject({ category: "busy", status: 503, serviceMessage: "every loaded model is busy; retry later" });

  expect(await failure(form, () => json({ error: { message: "insufficient memory", type: "insufficient_memory" } }, 503))).toMatchObject({ category: "http", status: 503 });
  expect(await failure(form, () => json({ error: { message: "model path does not exist", type: "server_error" } }, 500)))
    .toMatchObject({ category: "http", status: 500, serviceMessage: "model path does not exist" });
  expect(await failure(form, () => json({ kind: "proxy-unavailable", service: "audiocpp" }, 502))).toMatchObject({ category: "unavailable" });
  expect(await failure(form, () => json({ audio: "nope" }))).toMatchObject({ category: "protocol", message: "The service returned a success response that is not audio." });
  expect(await failure(form, () => new Response(new Uint8Array([82, 73, 70, 70]), { headers: { "content-type": "audio/wav" } }))).toMatchObject({ category: "protocol" });
});

test("a Busy run settles as its own neutral outcome", async () => {
  const busyAdapter = { ...adapter, execute: async () => { throw new ServiceFailure({ category: "busy", message: "audio.cpp is generating with another model; run again when it finishes.", status: 503, diagnostic: "503" }); } };
  const controller = new DetailRunController(busyAdapter);
  await controller.submit(values({ model: "voxcpm2", text: "Hello." }));
  expect(controller.snapshot.status).toBe("busy");
  expect(controller.snapshot.history[0]).toMatchObject({ outcome: "busy", title: "Busy", failureCategory: "busy", message: "audio.cpp is generating with another model; run again when it finishes." });
});

test("the input summary shows only the model's fields, a random seed, and the fixed values", () => {
  const summary = adapter.summarizeInput(values({ model: "chatterbox", text: "Hello.", reference_audio: reference("clip.wav"), design_prompt: "hidden" }));
  expect(summary.map(({ label }) => label)).toEqual(["Model", "Text to speak", "Reference audio", "Seed", "Fixed request values"]);
  expect(summary.find(({ label }) => label === "Seed")?.value).toBe("Random for this run");
  expect(summary.at(-1)?.value).toBe("language: en · exaggeration: 0.5 · guidance_scale: 0.5 · temperature: 0.8 · min_p: 0.05 · top_p: 1 · repetition_penalty: 1.2");
  expect(adapter.summarizeInput(values({ model: "qwen3-base", seed: "9" })).find(({ label }) => label === "Fixed request values")?.value).toBe("None");
});
