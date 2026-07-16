import { expect, test } from "@playwright/test";
import { createTtsAdapter, TURBO_TAGS, type FormValues, type ProgressEvent, type TtsServiceId } from "../src/service-adapter";
import { buildWav } from "./fixtures/wav-fixture";

const chatterbox = createTtsAdapter("chatterbox");
const turbo = createTtsAdapter("chatterbox-turbo");
const design = createTtsAdapter("qwen3-tts");
const base = createTtsAdapter("qwen3-tts-base");

function reference(name = "voice.wav", type = "audio/wav"): File {
  return new File([buildWav()], name, { type });
}

function wavResponse(bytes: Uint8Array = buildWav({ samples: 12 })): Response {
  return new Response(bytes, { status: 200, headers: { "content-type": "audio/wav" } });
}

function values(adapter: ReturnType<typeof createTtsAdapter>, overrides: Record<string, string | File | null> = {}): FormValues {
  return Object.freeze({ ...adapter.initialValues(), ...overrides }) as FormValues;
}

interface CapturedRequest { readonly url: string; readonly method: string; readonly entries: ReadonlyArray<readonly [string, string | File]> }

async function capture(adapter: ReturnType<typeof createTtsAdapter>, formValues: FormValues, response: Response = wavResponse()): Promise<{ request: CapturedRequest; execution: Awaited<ReturnType<typeof adapter.execute>> }> {
  let request: CapturedRequest | undefined;
  const execution = await adapter.execute(formValues, new AbortController().signal, () => {}, async (input, init) => {
    const body = init?.body;
    if (!(body instanceof FormData)) throw new Error("Expected a multipart FormData body.");
    request = { url: String(input), method: init?.method ?? "GET", entries: [...body.entries()] as ReadonlyArray<readonly [string, string | File]> };
    return response;
  });
  if (request === undefined) throw new Error("No request captured.");
  return { request, execution };
}

test("every direct-WAV adapter exposes its exact field inventory, defaults, and groups", () => {
  expect(chatterbox.fields.map(({ key, group, initialValue }) => [key, group, initialValue])).toEqual([
    ["text", "common", ""], ["reference_audio", "common", null],
    ["exaggeration", "advanced", "0.5"], ["cfg_weight", "advanced", "0.5"], ["temperature", "advanced", "0.8"],
    ["min_p", "advanced", "0.05"], ["top_p", "advanced", "1.0"], ["repetition_penalty", "advanced", "1.2"]
  ]);
  expect(turbo.fields.map(({ key, group, initialValue }) => [key, group, initialValue])).toEqual([
    ["text", "common", ""], ["reference_audio", "common", null],
    ["temperature", "advanced", "0.8"], ["repetition_penalty", "advanced", "1.2"]
  ]);
  expect(design.fields.map(({ key, group, initialValue }) => [key, group, initialValue])).toEqual([
    ["text", "common", ""], ["voice_description", "common", ""], ["language", "common", "auto"],
    ["temperature", "advanced", ""], ["top_p", "advanced", ""], ["top_k", "advanced", ""],
    ["repetition_penalty", "advanced", ""], ["max_new_tokens", "advanced", ""]
  ]);
  expect(base.fields.map(({ key, group, initialValue }) => [key, group, initialValue])).toEqual([
    ["text", "common", ""], ["reference_audio", "common", null], ["voice_transcript", "common", ""], ["language", "common", "auto"],
    ["temperature", "advanced", ""], ["top_p", "advanced", ""], ["top_k", "advanced", ""],
    ["repetition_penalty", "advanced", ""], ["max_new_tokens", "advanced", ""]
  ]);
  for (const adapter of [design, base]) {
    expect(adapter.fields.find(({ key }) => key === "language")?.options?.map(({ value }) => value))
      .toEqual(["auto", "en", "zh", "ja", "ko", "de", "fr", "ru", "pt", "es", "it"]);
  }
  for (const adapter of [chatterbox, turbo, base]) {
    expect(adapter.fields.find(({ key }) => key === "reference_audio")?.required).toBe(true);
  }
  expect(design.fields.some(({ control }) => control === "file")).toBe(false);
  expect(chatterbox.resultKind).toBe("audio");
});

test("Turbo documents every supported paralinguistic tag beside a tagged example", () => {
  const text = turbo.fields.find(({ key }) => key === "text");
  expect(TURBO_TAGS).toEqual(["[laugh]", "[chuckle]", "[sigh]", "[cough]", "[clear throat]", "[gasp]", "[groan]", "[sniff]", "[shush]"]);
  for (const tag of TURBO_TAGS) expect(text?.help, tag).toContain(tag);
  expect(text?.example).toContain("[sigh]");
});

test("required inputs block submission and cloning services require reference audio", () => {
  expect(chatterbox.validate(values(chatterbox)).errors).toMatchObject({ text: "Enter text to speak.", reference_audio: "Choose a reference audio file." });
  expect(chatterbox.validate(values(chatterbox, { text: "  ", reference_audio: reference() })).errors.text).toBe("Enter text to speak.");
  expect(chatterbox.validate(values(chatterbox, { text: "hello", reference_audio: new File([], "empty.wav", { type: "audio/wav" }) })).errors.reference_audio).toBe("Choose a reference audio file.");
  expect(design.validate(values(design, { text: "hello" })).errors).toMatchObject({ voice_description: "Enter voice description.", text: undefined });
  expect(base.validate(values(base, { text: "hello", reference_audio: reference() })).errors.voice_transcript).toBe("Enter reference transcript.");
  const valid = base.validate(values(base, { text: "hello", reference_audio: reference(), voice_transcript: "spoken words" }));
  expect(Object.values(valid.errors).filter((message) => message !== undefined)).toEqual([]);
});

test("numeric fields block only known-invalid shapes and never invent a bound the service lacks", () => {
  const filled = { text: "hello", reference_audio: reference() };
  expect(chatterbox.validate(values(chatterbox, { ...filled, min_p: "abc" })).errors.min_p).toBe("Enter a finite number.");
  expect(chatterbox.validate(values(chatterbox, { ...filled, top_p: "-3" })).errors.top_p).toBeUndefined();
  const qwen = design.validate(values(design, { text: "hello", voice_description: "warm" }));
  expect(Object.values(qwen.errors).filter((message) => message !== undefined)).toEqual([]);
  expect(design.validate(values(design, { text: "a", voice_description: "b", top_k: "2.5" })).errors.top_k).toBe("Enter a whole number.");
  expect(design.validate(values(design, { text: "a", voice_description: "b", top_k: "abc" })).errors.top_k).toBe("Enter a whole number.");
  // The services declare these as plain optional integers with no range check, so no bound is invented.
  expect(design.validate(values(design, { text: "a", voice_description: "b", max_new_tokens: "0" })).errors.max_new_tokens).toBeUndefined();
  expect(design.validate(values(design, { text: "a", voice_description: "b", max_new_tokens: "512", top_k: "40" })).errors.max_new_tokens).toBeUndefined();
  expect(design.validate(values(design, { text: "a", voice_description: "b", language: "klingon" })).errors.language).toBe("Choose a supported option.");
});

test("every sampling control is optional, so a cleared prefilled value is omitted rather than blocked", async () => {
  const filled = { text: "hello", reference_audio: reference() };
  for (const key of ["exaggeration", "cfg_weight", "temperature", "min_p", "top_p", "repetition_penalty"]) {
    expect(chatterbox.fields.find((field) => field.key === key)?.required, key).toBe(false);
    expect(chatterbox.validate(values(chatterbox, { ...filled, [key]: "" })).errors[key], key).toBeUndefined();
  }
  const { request } = await capture(chatterbox, values(chatterbox, { ...filled, exaggeration: "", cfg_weight: "   " }));
  expect(request.entries.map(([name]) => name)).toEqual(["text", "reference_audio", "temperature", "min_p", "top_p", "repetition_penalty"]);
  expect(turbo.validate(values(turbo, { ...filled, temperature: "" })).errors.temperature).toBeUndefined();
});

test("decoder-permissive extensions and 0-1 guidance warn without blocking", () => {
  const permissive = chatterbox.validate(values(chatterbox, { text: "hello", reference_audio: reference("voice.flac", "audio/flac") }));
  expect(permissive.errors.reference_audio).toBeUndefined();
  expect(permissive.warnings.reference_audio).toContain("WAV and MP3 are the documented inputs");
  const guidance = chatterbox.validate(values(chatterbox, { text: "hello", reference_audio: reference(), exaggeration: "1.4", cfg_weight: "-0.2" }));
  expect(guidance.errors.exaggeration).toBeUndefined();
  expect(guidance.warnings.exaggeration).toContain("documented range is 0 to 1");
  expect(guidance.warnings.cfg_weight).toContain("documented range is 0 to 1");
  expect(chatterbox.validate(values(chatterbox, { text: "hello", reference_audio: reference() })).warnings).toEqual({});
});

test("Chatterbox sends every prefilled control as exact multipart name, value, file, and bytes", async () => {
  const file = reference("clone.wav");
  const { request, execution } = await capture(chatterbox, values(chatterbox, { text: " Speak this. ", reference_audio: file }));
  expect(request.url).toBe("/proxy/chatterbox/tts");
  expect(request.method).toBe("POST");
  expect(request.entries.map(([name]) => name)).toEqual(["text", "reference_audio", "exaggeration", "cfg_weight", "temperature", "min_p", "top_p", "repetition_penalty"]);
  expect(Object.fromEntries(request.entries.filter(([, value]) => typeof value === "string"))).toEqual({
    text: " Speak this. ", exaggeration: "0.5", cfg_weight: "0.5", temperature: "0.8", min_p: "0.05", top_p: "1.0", repetition_penalty: "1.2"
  });
  const sent = request.entries.find(([name]) => name === "reference_audio")?.[1] as File;
  expect(sent.name).toBe("clone.wav");
  expect(sent.type).toBe("audio/wav");
  expect(new Uint8Array(await sent.arrayBuffer())).toEqual(buildWav());
  expect(execution.result).toMatchObject({ kind: "audio", mediaType: "audio/wav", sampleRate: 24_000 });
});

test("Turbo posts its own route and only its two Advanced controls", async () => {
  const { request } = await capture(turbo, values(turbo, { text: "Well now. [sigh] Again.", reference_audio: reference() }));
  expect(request.url).toBe("/proxy/chatterbox-turbo/tts/turbo");
  expect(request.entries.map(([name]) => name)).toEqual(["text", "reference_audio", "temperature", "repetition_penalty"]);
  expect(Object.fromEntries(request.entries.filter(([, value]) => typeof value === "string"))).toEqual({
    text: "Well now. [sigh] Again.", temperature: "0.8", repetition_penalty: "1.2"
  });
});

test("blank Qwen sampling values are omitted and set values are sent unchanged", async () => {
  const blank = await capture(design, values(design, { text: "hello", voice_description: "A warm narrator" }));
  expect(blank.request.url).toBe("/proxy/qwen3-tts/tts");
  expect(blank.request.entries).toEqual([["text", "hello"], ["voice_description", "A warm narrator"], ["language", "auto"]]);

  const set = await capture(design, values(design, {
    text: "hello", voice_description: "A warm narrator", language: "en",
    temperature: "0.7", top_p: "0.9", top_k: "40", repetition_penalty: "1.1", max_new_tokens: "512"
  }));
  expect(set.request.entries).toEqual([
    ["text", "hello"], ["voice_description", "A warm narrator"], ["language", "en"],
    ["temperature", "0.7"], ["top_p", "0.9"], ["top_k", "40"], ["repetition_penalty", "1.1"], ["max_new_tokens", "512"]
  ]);

  const cleared = await capture(design, values(design, { text: "hello", voice_description: "A warm narrator", temperature: "   ", top_k: "" }));
  expect(cleared.request.entries.map(([name]) => name)).toEqual(["text", "voice_description", "language"]);
});

test("Qwen Base sends the reference clip and its exact transcript", async () => {
  const { request } = await capture(base, values(base, { text: "hello", reference_audio: reference("ref.mp3", "audio/mpeg"), voice_transcript: "  exact words  ", language: "ja" }));
  expect(request.url).toBe("/proxy/qwen3-tts-base/tts");
  expect(request.entries.map(([name]) => name)).toEqual(["text", "reference_audio", "voice_transcript", "language"]);
  expect(request.entries.find(([name]) => name === "voice_transcript")?.[1]).toBe("  exact words  ");
  expect((request.entries.find(([name]) => name === "reference_audio")?.[1] as File).type).toBe("audio/mpeg");
});

test("successful audio results carry a safe UTC filename, WAV blob, and phase-only progress", async () => {
  const events: ProgressEvent[] = [];
  const execution = await chatterbox.execute(
    values(chatterbox, { text: "prompt derived name must not leak", reference_audio: reference("secret-name.wav") }),
    new AbortController().signal, (event) => events.push(event), async () => wavResponse(buildWav({ sampleRate: 16_000, samples: 6 }))
  );
  expect(events.map((event) => event.kind === "phase" ? `${event.phase}:${event.status}` : event.kind))
    .toEqual(["request:started", "request:completed", "generate:started", "generate:completed"]);
  expect(events.every((event) => !("percent" in event))).toBe(true);
  if (execution.result.kind !== "audio") throw new Error("Expected an audio result.");
  expect(execution.result.filename).toMatch(/^read2me-chatterbox-\d{8}T\d{6}Z\.wav$/u);
  expect(execution.result.blob.type).toBe("audio/wav");
  expect(execution.result.blob.size).toBe(buildWav({ sampleRate: 16_000, samples: 6 }).byteLength);
  expect(execution.result.sampleRate).toBe(16_000);
  expect(execution.warnings).toEqual([]);
});

test("diagnostics record headers, byte count, and parsed format but never audio bytes", async () => {
  const bytes = buildWav({ samples: 64 });
  const { execution } = await capture(chatterbox, values(chatterbox, { text: "diagnostics", reference_audio: reference() }), wavResponse(bytes));
  expect(execution.diagnostic).toContain("POST /proxy/chatterbox/tts");
  expect(execution.diagnostic).toContain("Content-Type: audio/wav");
  expect(execution.diagnostic).toContain(`Bytes: ${bytes.byteLength}`);
  expect(execution.diagnostic).toContain("WAV format 1 · 1 channel · 24000 Hz · 16-bit · 128 data bytes");
  expect(execution.diagnostic).not.toContain("RIFF");
  expect([...execution.diagnostic].every((character) => character === "\n" || (character.codePointAt(0) ?? 0) >= 32)).toBe(true);
  expect(execution.diagnostic.length).toBeLessThan(2_048);
});

for (const id of ["chatterbox", "chatterbox-turbo", "qwen3-tts", "qwen3-tts-base"] as const) {
  test(`${id} maps service, media, WAV, network, and cancellation failures correctly`, async () => {
    const adapter = createTtsAdapter(id as TtsServiceId);
    const filled = values(adapter, { text: "hello", reference_audio: reference(), voice_description: "warm", voice_transcript: "words" });
    const run = async (responder: () => Promise<Response>): Promise<unknown> =>
      adapter.execute(filled, new AbortController().signal, () => {}, responder);

    await expect(run(async () => new Response(JSON.stringify({ detail: "text must not be empty" }), { status: 422, headers: { "content-type": "application/json" } })))
      .rejects.toMatchObject({ category: "http", status: 422, serviceMessage: "text must not be empty" });
    await expect(run(async () => new Response(JSON.stringify({ kind: "proxy-unavailable", service: id, message: "connect ECONNREFUSED" }), { status: 502, headers: { "content-type": "application/json" } })))
      .rejects.toMatchObject({ category: "unavailable", status: 502 });
    await expect(run(async () => new Response("Internal Server Error", { status: 500, headers: { "content-type": "text/plain" } })))
      .rejects.toMatchObject({ category: "http", status: 500 });
    await expect(run(async () => new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "content-type": "application/json" } })))
      .rejects.toMatchObject({ category: "protocol", message: "The service returned a success response that is not audio." });
    await expect(run(async () => wavResponse(buildWav().subarray(0, 30))))
      .rejects.toMatchObject({ category: "protocol", message: /invalid WAV audio/u });
    await expect(run(async () => wavResponse(new Uint8Array([0x52, 0x49, 0x46, 0x46, 1, 0, 0, 0, 0x57, 0x41, 0x56, 0x45]))))
      .rejects.toMatchObject({ category: "protocol", message: /invalid WAV audio/u });
    await expect(run(async () => { throw new TypeError("Failed to fetch"); }))
      .rejects.toMatchObject({ category: "unavailable", message: `The dashboard could not reach ${adapter.name} through the local proxy.` });

    const controller = new AbortController();
    let observed = false;
    const pending = adapter.execute(filled, controller.signal, () => {}, async (_input, init) => await new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => { observed = true; reject(new DOMException("aborted", "AbortError")); }, { once: true });
    }));
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
    expect(observed).toBe(true);
  });
}

test("reached-service error diagnostics stay bounded at 64 KiB with a truncation marker", async () => {
  await expect(chatterbox.execute(
    values(chatterbox, { text: "hello", reference_audio: reference() }), new AbortController().signal, () => {},
    async () => new Response(`{"detail":"${"x".repeat(70 * 1_024)}"}`, { status: 500, headers: { "content-type": "application/json" } })
  )).rejects.toMatchObject({ category: "http", status: 500, diagnostic: /\[truncated\]$/u });
});

test("input summaries bound text and reduce files to name, size, and MIME without retaining bytes", () => {
  const summary = base.summarizeInput(values(base, { text: "x".repeat(5_000), reference_audio: reference("ref.wav"), voice_transcript: "words" }));
  expect(summary[0]?.value).toHaveLength(4_096);
  expect(summary[0]?.value.endsWith("\n[truncated]")).toBe(true);
  expect(summary[1]).toEqual({ label: "Reference audio", value: `ref.wav · ${buildWav().byteLength} bytes · audio/wav` });
  expect(summary.some((item) => item.value instanceof File)).toBe(false);
  expect(Object.isFrozen(summary)).toBe(true);
});
