import { expect, test } from "@playwright/test";
import { createWhisperAdapter, type FormValues, type ProgressEvent } from "../src/service-adapter";
import { CANONICAL_WAV, describeCanonicalDifference } from "../src/whisper";
import { buildWav } from "./fixtures/wav-fixture";

const whisper = createWhisperAdapter();

/** The known-working Read2Me shape: Canonical WAV at 24 kHz mono PCM16. */
function audio(name = "speech.wav", type = "audio/wav", bytes: Uint8Array = buildWav({ samples: 32 })): File {
  return new File([bytes], name, { type });
}

function values(overrides: Record<string, string | boolean | File | null> = {}): FormValues {
  return Object.freeze({ ...whisper.initialValues(), file: audio(), ...overrides }) as FormValues;
}

function jsonResponse(payload: unknown, status = 200): Response {
  return new Response(typeof payload === "string" ? payload : JSON.stringify(payload), { status, headers: { "content-type": "application/json" } });
}

function textResponse(body: string): Response {
  return new Response(body, { status: 200, headers: { "content-type": "text/plain" } });
}

const VERBOSE_PAYLOAD = {
  task: "transcribe",
  language: "en",
  duration: 2.5,
  text: " It was a bright cold day.",
  segments: [
    {
      id: 0, start: 0, end: 1.2, text: " It was a",
      words: [
        { word: " It", start: 0, end: 0.3, probability: 0.98 },
        { word: " was", start: 0.3, end: 0.7, probability: 0.91 },
        { word: " a", start: 0.7, end: 1.2, probability: 0.88 }
      ]
    },
    {
      id: 1, start: 1.2, end: 2.5, text: " bright cold day.",
      words: [
        { word: " bright", start: 1.2, end: 1.8, probability: 0.95 },
        { word: " cold", start: 1.8, end: 2.1 },
        { word: " day.", start: 2.1, end: 2.5, probability: 0.99 }
      ]
    }
  ]
};

interface CapturedRequest { readonly url: string; readonly method: string; readonly entries: ReadonlyArray<readonly [string, string | File]> }

async function capture(formValues: FormValues, response: Response = jsonResponse(VERBOSE_PAYLOAD)): Promise<{ request: CapturedRequest; execution: Awaited<ReturnType<typeof whisper.execute>> }> {
  let request: CapturedRequest | undefined;
  const execution = await whisper.execute(formValues, new AbortController().signal, () => {}, async (input, init) => {
    const body = init?.body;
    if (!(body instanceof FormData)) throw new Error("Expected a multipart FormData body.");
    request = { url: String(input), method: init?.method ?? "GET", entries: [...body.entries()] as ReadonlyArray<readonly [string, string | File]> };
    return response;
  });
  if (request === undefined) throw new Error("No request captured.");
  return { request, execution };
}

async function run(formValues: FormValues, response: Response): Promise<Awaited<ReturnType<typeof whisper.execute>>> {
  return await whisper.execute(formValues, new AbortController().signal, () => {}, async () => response);
}

test("the common group carries the known-working word-alignment confirmation defaults", () => {
  expect(whisper.fields.filter(({ group }) => group === "common").map(({ key, control, initialValue, required }) => [key, control, initialValue, required])).toEqual([
    ["file", "file", null, true],
    ["response_format", "select", "verbose_json", true],
    ["language", "text", "en", true],
    ["token_timestamps", "checkbox", true, false]
  ]);
  expect(whisper.fields.find(({ key }) => key === "response_format")?.options?.map(({ value }) => value))
    .toEqual(["json", "verbose_json", "text", "srt", "vtt"]);
  expect(whisper.resultKind).toBe("transcription");
  expect(whisper.id).toBe("whisper");
});

test("every verified Advanced field appears in its labelled group with the exact spec default", () => {
  const grouped = new Map<string, Array<readonly [string, unknown]>>();
  for (const field of whisper.fields.filter(({ group }) => group === "advanced")) {
    const list = grouped.get(field.advancedGroup ?? "") ?? [];
    list.push([field.key, field.initialValue]);
    grouped.set(field.advancedGroup ?? "", list);
  }
  expect([...grouped.keys()]).toEqual([
    "Slicing and context", "Decoding", "Language and task", "Timing and output", "Speech and speakers", "Voice activity detection"
  ]);
  expect(grouped.get("Slicing and context")).toEqual([
    ["offset_t", "0"], ["offset_n", "0"], ["duration", "0"], ["max_context", "-1"], ["max_len", "1"], ["audio_ctx", "0"]
  ]);
  expect(grouped.get("Decoding")).toEqual([
    ["best_of", "2"], ["beam_size", "-1"], ["temperature", "0"], ["temperature_inc", "0.2"],
    ["entropy_thold", "2.4"], ["logprob_thold", "-1"], ["no_speech_thold", "0.6"], ["word_thold", "0.01"]
  ]);
  expect(grouped.get("Language and task")).toEqual([
    ["translate", false], ["detect_language", false], ["prompt", ""], ["carry_initial_prompt", false]
  ]);
  expect(grouped.get("Timing and output")).toEqual([
    ["no_timestamps", false], ["split_on_word", true], ["no_language_probabilities", false]
  ]);
  expect(grouped.get("Speech and speakers")).toEqual([
    ["diarize", false], ["tinydiarize", false], ["suppress_non_speech", false], ["suppress_nst", false], ["debug_mode", false]
  ]);
  expect(grouped.get("Voice activity detection")).toEqual([
    ["vad", false], ["vad_threshold", "0.5"], ["vad_min_speech_duration_ms", "250"], ["vad_min_silence_duration_ms", "100"],
    ["vad_max_speech_duration_s", "3.402823466e38"], ["vad_speech_pad_ms", "30"], ["vad_samples_overlap", "0.1"]
  ]);
});

test("the file picker advertises WAV only and never promises MP3 or AAC conversion", () => {
  const file = whisper.fields.find(({ key }) => key === "file");
  expect(file?.accept).toBe(".wav,audio/wav");
  const help = whisper.fields.map((field) => `${field.help ?? ""} ${field.example ?? ""}`).join(" ").toLowerCase();
  expect(help).not.toContain("mp3");
  expect(help).not.toContain("aac");
  expect(help).not.toContain("convert");
  expect(whisper.fields.some(({ key }) => key === "load")).toBe(false);
});

test("a missing audio file blocks the run and a non-WAV choice warns without blocking", () => {
  expect(whisper.validate(Object.freeze({ ...whisper.initialValues(), file: null }) as FormValues).errors.file).toBe("Choose a WAV audio file.");
  expect(whisper.validate(values({ file: new File([], "empty.wav", { type: "audio/wav" }) })).errors.file).toBe("Choose a WAV audio file.");
  const permissive = whisper.validate(values({ file: audio("speech.mp3", "audio/mpeg") }));
  expect(permissive.errors.file).toBeUndefined();
  expect(permissive.warnings.file).toContain("WAV is the only supported input");
  expect(whisper.validate(values()).warnings.file).toBeUndefined();
});

test("numeric Advanced fields block only known-invalid shapes and never invent an unenforced bound", () => {
  expect(whisper.validate(values({ best_of: "abc" })).errors.best_of).toBe("Enter a whole number.");
  expect(whisper.validate(values({ best_of: "2.5" })).errors.best_of).toBe("Enter a whole number.");
  expect(whisper.validate(values({ temperature: "abc" })).errors.temperature).toBe("Enter a finite number.");
  // The service enforces no ranges of its own, so negative and large values are sent unchanged.
  expect(whisper.validate(values({ beam_size: "-1", max_context: "-1", logprob_thold: "-99" })).errors.beam_size).toBeUndefined();
  expect(whisper.validate(values({ vad_max_speech_duration_s: "3.402823466e38" })).errors.vad_max_speech_duration_s).toBeUndefined();
  expect(whisper.validate(values({ temperature: "" })).errors.temperature).toBeUndefined();
  expect(Object.values(whisper.validate(values()).errors).filter((message) => message !== undefined)).toEqual([]);
});

test("timing conflicts, non-English work, and enabled VAD warn rather than rewrite the operator's values", () => {
  const suppressed = whisper.validate(values({ no_timestamps: true }));
  expect(suppressed.errors.no_timestamps).toBeUndefined();
  expect(suppressed.warnings.no_timestamps).toContain("no timings");

  expect(whisper.validate(values({ split_on_word: true, token_timestamps: false })).warnings.split_on_word).toContain("Word timestamps");
  expect(whisper.validate(values({ no_timestamps: false })).warnings.no_timestamps).toBeUndefined();

  const foreign = whisper.validate(values({ language: "fr" }));
  expect(foreign.errors.language).toBeUndefined();
  expect(foreign.warnings.language).toContain("base.en");
  expect(whisper.validate(values({ language: "en" })).warnings.language).toBeUndefined();

  const detect = whisper.validate(values({ detect_language: true }));
  expect(detect.errors.detect_language).toBeUndefined();
  expect(detect.warnings.detect_language).toContain("base.en");

  const translate = whisper.validate(values({ translate: true }));
  expect(translate.warnings.translate).toContain("base.en");

  const vad = whisper.validate(values({ vad: true }));
  expect(vad.errors.vad).toBeUndefined();
  expect(vad.warnings.vad).toContain("no VAD model");
  expect(whisper.validate(values()).warnings.vad).toBeUndefined();
});

test("the request is one multipart POST carrying the exact confirmation defaults", async () => {
  const file = audio("fixture.wav");
  const { request } = await capture(values({ file }));
  expect(request.url).toBe("/proxy/whisper/inference");
  expect(request.method).toBe("POST");
  const sent = Object.fromEntries(request.entries.filter(([, value]) => typeof value === "string"));
  expect(sent).toMatchObject({
    response_format: "verbose_json", language: "en", token_timestamps: "true", max_len: "1", split_on_word: "true"
  });
  // Every prefilled control and every checkbox is sent explicitly, and the blank prompt is omitted.
  expect(sent).toMatchObject({
    offset_t: "0", offset_n: "0", duration: "0", max_context: "-1", audio_ctx: "0",
    best_of: "2", beam_size: "-1", temperature: "0", temperature_inc: "0.2", entropy_thold: "2.4",
    logprob_thold: "-1", no_speech_thold: "0.6", word_thold: "0.01",
    translate: "false", detect_language: "false", carry_initial_prompt: "false",
    no_timestamps: "false", no_language_probabilities: "false",
    diarize: "false", tinydiarize: "false", suppress_non_speech: "false", suppress_nst: "false", debug_mode: "false",
    vad: "false", vad_threshold: "0.5", vad_min_speech_duration_ms: "250", vad_min_silence_duration_ms: "100",
    vad_max_speech_duration_s: "3.402823466e38", vad_speech_pad_ms: "30", vad_samples_overlap: "0.1"
  });
  expect(Object.hasOwn(sent, "prompt")).toBe(false);
  expect(request.entries[0]?.[0]).toBe("file");
  const uploaded = request.entries.find(([name]) => name === "file")?.[1] as File;
  expect(uploaded.name).toBe("fixture.wav");
  expect(new Uint8Array(await uploaded.arrayBuffer())).toEqual(buildWav({ samples: 32 }));
});

test("independently edited Advanced values reach the wire and a cleared optional field is omitted", async () => {
  const { request } = await capture(values({
    no_timestamps: true, split_on_word: false, token_timestamps: false, best_of: "5", temperature: "0.4",
    prompt: "Read2Me chapter one", vad: true, max_len: "", language: "fr"
  }));
  const sent = Object.fromEntries(request.entries.filter(([, value]) => typeof value === "string"));
  expect(sent).toMatchObject({
    no_timestamps: "true", split_on_word: "false", token_timestamps: "false", best_of: "5", temperature: "0.4",
    prompt: "Read2Me chapter one", vad: "true", language: "fr"
  });
  // Nothing silently rewrites a conflicting combination back to the confirmation default.
  expect(Object.hasOwn(sent, "max_len")).toBe(false);
});

test("verbose JSON yields transcript, ordered words, and ordered segment metadata without repair", async () => {
  const { execution } = await capture(values());
  if (execution.result.kind !== "transcription") throw new Error("Expected a transcription result.");
  expect(execution.result.format).toBe("verbose_json");
  expect(execution.result.text).toBe(" It was a bright cold day.");
  expect(execution.result.language).toBe("en");
  expect(execution.result.duration).toBe(2.5);
  expect(execution.result.segments).toEqual([
    { text: " It was a", start: 0, end: 1.2 },
    { text: " bright cold day.", start: 1.2, end: 2.5 }
  ]);
  // Service order is preserved verbatim and the word without a probability keeps none.
  expect(execution.result.words).toEqual([
    { text: " It", start: 0, end: 0.3, probability: 0.98 },
    { text: " was", start: 0.3, end: 0.7, probability: 0.91 },
    { text: " a", start: 0.7, end: 1.2, probability: 0.88 },
    { text: " bright", start: 1.2, end: 1.8, probability: 0.95 },
    { text: " cold", start: 1.8, end: 2.1 },
    { text: " day.", start: 2.1, end: 2.5, probability: 0.99 }
  ]);
  expect(execution.warnings).toEqual([]);
});

test("plain JSON returns only the transcript and reports no timings it did not receive", async () => {
  const { execution } = await capture(values({ response_format: "json" }), jsonResponse({ text: "  spaced   transcript  " }));
  if (execution.result.kind !== "transcription") throw new Error("Expected a transcription result.");
  expect(execution.result.format).toBe("json");
  expect(execution.result.text).toBe("  spaced   transcript  ");
  expect(execution.result.words).toBeUndefined();
  expect(execution.result.segments).toBeUndefined();
});

for (const [format, body] of [
  ["text", " It was a bright cold day.\n"],
  ["srt", "1\n00:00:00,000 --> 00:00:01,200\n It was a\n\n2\n00:00:01,200 --> 00:00:02,500\n bright cold day.\n\n"],
  ["vtt", "WEBVTT\n\n00:00:00.000 --> 00:00:01.200\n It was a\n\n00:00:01.200 --> 00:00:02.500\n bright cold day.\n\n"]
] as const) {
  test(`${format} responses are preserved verbatim, whitespace included`, async () => {
    const execution = await run(values({ response_format: format }), textResponse(body));
    if (execution.result.kind !== "transcription") throw new Error("Expected a transcription result.");
    expect(execution.result.format).toBe(format);
    expect(execution.result.text).toBe(body);
    expect(execution.result.words).toBeUndefined();
  });
}

test("a valid response without words is a warned success rather than a failure", async () => {
  const execution = await run(values(), jsonResponse({ text: "no alignment", segments: [{ start: 0, end: 1, text: "no alignment" }] }));
  expect(execution.result.kind).toBe("transcription");
  if (execution.result.kind !== "transcription") throw new Error("Expected a transcription result.");
  expect(execution.result.words).toBeUndefined();
  expect(execution.warnings.join(" ")).toContain("no word timings");
  // Requesting no word timings makes their absence unremarkable.
  const quiet = await run(values({ token_timestamps: false }), jsonResponse({ text: "no alignment", segments: [] }));
  expect(quiet.warnings).toEqual([]);
});

test("a recognizable non-Canonical WAV warns on success and never blocks or repairs the upload", async () => {
  expect(CANONICAL_WAV).toEqual({ sampleRate: 24_000, channels: 1, bitsPerSample: 16, audioFormat: 1 });
  const stereo = buildWav({ channels: 2, sampleRate: 44_100, samples: 16 });
  const { execution } = await capture(values({ file: audio("stereo.wav", "audio/wav", stereo) }));
  expect(execution.result.kind).toBe("transcription");
  expect(execution.warnings.join(" ")).toContain("24000 Hz");
  expect(execution.warnings.join(" ")).toContain("Canonical WAV");
  expect((await capture(values())).execution.warnings).toEqual([]);

  // Bytes that are not recognizable WAV are still uploaded, with a warning rather than a block.
  const unrecognized = await capture(values({ file: audio("speech.wav", "audio/wav", new Uint8Array([1, 2, 3, 4])) }));
  expect(unrecognized.execution.warnings.join(" ")).toContain("not recognizable WAV");
  expect(unrecognized.request.entries.some(([name]) => name === "file")).toBe(true);

  expect(describeCanonicalDifference({ audioFormat: 1, channels: 1, sampleRate: 24_000, bitsPerSample: 16, dataBytes: 8 })).toBeUndefined();
  expect(describeCanonicalDifference({ audioFormat: 1, channels: 2, sampleRate: 24_000, bitsPerSample: 16, dataBytes: 8 })).toContain("1 channel");
});

test("malformed JSON and non-finite or reversed timings fail as protocol errors", async () => {
  await expect(run(values(), jsonResponse("{not-json", 200)))
    .rejects.toMatchObject({ category: "protocol", message: /malformed|invalid/u });
  await expect(run(values(), jsonResponse({ segments: [] })))
    .rejects.toMatchObject({ category: "protocol" });
  await expect(run(values(), new Response(JSON.stringify(VERBOSE_PAYLOAD), { status: 200, headers: { "content-type": "text/html" } })))
    .rejects.toMatchObject({ category: "protocol" });

  const reversed = { text: "x", segments: [{ start: 2, end: 1, text: "x", words: [] }] };
  await expect(run(values(), jsonResponse(reversed))).rejects.toMatchObject({ category: "protocol", message: /timing/u });

  const reversedWord = { text: "x", segments: [{ start: 0, end: 2, text: "x", words: [{ word: "x", start: 1.5, end: 0.5 }] }] };
  await expect(run(values(), jsonResponse(reversedWord))).rejects.toMatchObject({ category: "protocol", message: /timing/u });

  const nonFinite = { text: "x", segments: [{ start: 0, end: 2, text: "x", words: [{ word: "x", start: 0, end: null }] }] };
  await expect(run(values(), jsonResponse(nonFinite))).rejects.toMatchObject({ category: "protocol", message: /timing/u });

  await expect(run(values(), jsonResponse(`{"text":"x","duration":1e999,"segments":[]}`)))
    .rejects.toMatchObject({ category: "protocol", message: /timing/u });
});

test("service, proxy, network, and cancellation failures map to their categories", async () => {
  await expect(run(values(), jsonResponse({ error: "failed to read audio file" }, 400)))
    .rejects.toMatchObject({ category: "http", status: 400, serviceMessage: "failed to read audio file" });
  await expect(run(values(), jsonResponse({ kind: "proxy-unavailable", service: "whisper", message: "connect ECONNREFUSED" }, 502)))
    .rejects.toMatchObject({ category: "unavailable", status: 502 });
  await expect(run(values(), new Response("Internal Server Error", { status: 500, headers: { "content-type": "text/plain" } })))
    .rejects.toMatchObject({ category: "http", status: 500 });
  await expect(whisper.execute(values(), new AbortController().signal, () => {}, async () => { throw new TypeError("Failed to fetch"); }))
    .rejects.toMatchObject({ category: "unavailable", message: "The dashboard could not reach Whisper.cpp through the local proxy." });

  // Cancelling an in-flight transcription propagates the abort to the request the service is holding.
  const controller = new AbortController();
  let observed = false;
  const pending = whisper.execute(values(), controller.signal, () => {}, async (_input, init) => await new Promise<Response>((_resolve, reject) => {
    init?.signal?.addEventListener("abort", () => { observed = true; reject(new DOMException("aborted", "AbortError")); }, { once: true });
    controller.abort();
  }));
  await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  expect(observed).toBe(true);

  // Cancelling before the request leaves the wire settles the run without ever contacting the service.
  const early = new AbortController();
  let contacted = false;
  const abandoned = whisper.execute(values(), early.signal, () => {}, async () => { contacted = true; return jsonResponse(VERBOSE_PAYLOAD); });
  early.abort();
  await expect(abandoned).rejects.toMatchObject({ name: "AbortError" });
  expect(contacted).toBe(false);
});

test("progress reports upload and request phases without inventing a percentage", async () => {
  const events: ProgressEvent[] = [];
  await whisper.execute(values(), new AbortController().signal, (event) => events.push(event), async () => jsonResponse(VERBOSE_PAYLOAD));
  expect(events.map((event) => event.kind === "phase" ? `${event.phase}:${event.status}` : event.kind))
    .toEqual(["upload:started", "upload:completed", "request:started", "request:completed"]);
  expect(events.every((event) => !("percent" in event))).toBe(true);
});

test("diagnostics stay bounded and record the request shape and response body", async () => {
  const { execution } = await capture(values());
  expect(execution.diagnostic).toContain("POST /proxy/whisper/inference");
  expect(execution.diagnostic).toContain("response_format: verbose_json");
  expect(execution.diagnostic).toContain("It was a bright cold day.");
  await expect(run(values(), jsonResponse(`{"error":"${"x".repeat(70 * 1_024)}"}`, 500)))
    .rejects.toMatchObject({ category: "http", status: 500, diagnostic: /\[truncated\]$/u });
});

test("input summaries bound text and reduce the upload to name, size, and MIME without retaining bytes", () => {
  const summary = whisper.summarizeInput(values({ prompt: "x".repeat(5_000) }));
  expect(summary[0]).toEqual({ label: "Audio file", value: `speech.wav · ${buildWav({ samples: 32 }).byteLength} bytes · audio/wav` });
  expect(summary.find((item) => item.label === "Initial prompt")?.value).toHaveLength(4_096);
  expect(summary.some((item) => item.value instanceof File)).toBe(false);
  expect(Object.isFrozen(summary)).toBe(true);
});
