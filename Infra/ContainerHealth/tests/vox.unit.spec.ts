import { expect, test } from "@playwright/test";
import { createVoxAdapter, type FormValues, type ProgressEvent } from "../src/service-adapter";
import { buildPcm16Wav, isSupportedVoxUpload, parseVoxStream, VOX_UPLOAD_LIMIT_BYTES } from "../src/vox";
import { parseWav } from "../src/tts";
import { buildWav } from "./fixtures/wav-fixture";
import { chunkedStream, concat, controlFrame, frame, pcmFrame, pendingStream, uploadResponse, voxBytes, voxStreamResponse } from "./fixtures/vox-fixture";

const vox = createVoxAdapter();
const noProgress = (): void => {};

function reference(name = "voice.wav", type = "audio/wav", bytes: Uint8Array = buildWav()): File {
  return new File([bytes], name, { type });
}

function values(overrides: Record<string, string | boolean | File | null> = {}): FormValues {
  return Object.freeze({ ...vox.initialValues(), reference_audio: reference(), text: "hello", ...overrides }) as FormValues;
}

async function parse(bytes: Uint8Array, boundaries: readonly number[] = []): Promise<Awaited<ReturnType<typeof parseVoxStream>>> {
  return parseVoxStream(chunkedStream(bytes, boundaries), new AbortController().signal);
}

async function expectParseFailure(bytes: Uint8Array, expected: Record<string, unknown>): Promise<void> {
  await expect(parse(bytes)).rejects.toMatchObject(expected);
}

test("the parser reassembles a complete exchange across every possible split point", async () => {
  const bytes = voxBytes([0, 0.25, -0.25, 1]);
  for (let split = 1; split < bytes.byteLength; split += 1) {
    const parsed = await parse(bytes, [split]);
    expect(parsed.sampleRate, `split at ${split}`).toBe(24_000);
    expect([...parsed.samples], `split at ${split}`).toEqual([0, 0.25, -0.25, 1]);
  }
});

test("the parser survives byte-at-a-time delivery and every three-way split of a header", async () => {
  const bytes = voxBytes([0.5, -0.5]);
  const everyByte = await parse(bytes, [...bytes.keys()]);
  expect([...everyByte.samples]).toEqual([0.5, -0.5]);
  // The five-byte header of the second frame starts at 5 + meta payload; split inside it repeatedly.
  const metaLength = controlFrame({ type: "meta", sample_rate: 24_000 }).byteLength;
  for (let first = metaLength; first < metaLength + 5; first += 1) {
    for (let second = first + 1; second < metaLength + 6; second += 1) {
      const parsed = await parse(bytes, [first, second]);
      expect([...parsed.samples], `splits ${first}/${second}`).toEqual([0.5, -0.5]);
    }
  }
});

test("multi-frame audio concatenates in order and zero PCM frames still succeed", async () => {
  const multi = await parse(concat(
    controlFrame({ type: "meta", sample_rate: 16_000 }),
    pcmFrame([0.1, 0.2]), pcmFrame([0.3]), pcmFrame([]), pcmFrame([0.4, 0.5]),
    controlFrame({ type: "done" })
  ), [7, 19, 30]);
  expect(multi.sampleRate).toBe(16_000);
  expect([...multi.samples].map((value) => Number(value.toFixed(4)))).toEqual([0.1, 0.2, 0.3, 0.4, 0.5]);

  const silent = await parse(concat(controlFrame({ type: "meta", sample_rate: 24_000 }), controlFrame({ type: "done" })));
  expect(silent.samples.length).toBe(0);
  expect(parseWav(buildPcm16Wav(silent.samples, silent.sampleRate)).ok).toBe(true);
});

test("a framed error becomes the service's own message rather than a protocol failure", async () => {
  await expectParseFailure(
    concat(controlFrame({ type: "error", message: "text is required" }), controlFrame({ type: "done" })),
    { category: "http", message: "text is required", serviceMessage: "text is required" }
  );
  await expectParseFailure(
    concat(controlFrame({ type: "meta", sample_rate: 24_000 }), pcmFrame([0.1]), controlFrame({ type: "error", message: "model not loaded" })),
    { category: "http", serviceMessage: "model not loaded" }
  );
});

test("unknown, impossible, duplicate, and out-of-order frames are protocol failures", async () => {
  const meta = controlFrame({ type: "meta", sample_rate: 24_000 });
  await expectParseFailure(concat(meta, frame(7, new Uint8Array([1, 2])), controlFrame({ type: "done" })),
    { category: "protocol", message: "The service sent an unknown frame type: 7." });
  await expectParseFailure(concat(meta, controlFrame({ type: "surprise" }), controlFrame({ type: "done" })),
    { category: "protocol", message: "The service sent an unknown control frame: surprise." });
  // A declared length no real frame can reach must fail rather than attempt the allocation.
  await expectParseFailure(concat(meta, new Uint8Array([1, 0xff, 0xff, 0xff, 0xff])),
    { category: "protocol", message: /impossible frame length of 4294967295 bytes/u });
  await expectParseFailure(concat(meta, meta, controlFrame({ type: "done" })),
    { category: "protocol", message: "The service sent a duplicate meta frame." });
  await expectParseFailure(concat(pcmFrame([0.1]), meta, controlFrame({ type: "done" })),
    { category: "protocol", message: "The service sent audio before reporting its audio format." });
  await expectParseFailure(concat(meta, controlFrame({ type: "done" }), pcmFrame([0.1])),
    { category: "protocol", message: "The service sent a frame after completing the stream." });
  await expectParseFailure(concat(meta, frame(1, new Uint8Array([1, 2, 3])), controlFrame({ type: "done" })),
    { category: "protocol", message: "The service sent an audio frame that is not whole float32 samples." });
});

test("truncated frames and missing terminal frames report an incomplete response", async () => {
  const complete = voxBytes([0.5]);
  for (const cut of [3, 5, 9, complete.byteLength - 4, complete.byteLength - 1]) {
    await expectParseFailure(complete.slice(0, cut), { category: "protocol", message: "The service response ended before completion." });
  }
  await expectParseFailure(concat(controlFrame({ type: "meta", sample_rate: 24_000 }), pcmFrame([0.5])),
    { category: "protocol", message: "The service response ended before completion." });
  await expectParseFailure(new Uint8Array(0), { category: "protocol", message: "The service response ended before completion." });
  await expectParseFailure(concat(controlFrame({ type: "done" })),
    { category: "protocol", message: "The service ended the stream before reporting its audio format." });
});

test("malformed control frames and invalid sample rates fail without audio", async () => {
  await expectParseFailure(concat(frame(0, new TextEncoder().encode("{not json")), controlFrame({ type: "done" })),
    { category: "protocol", message: "The service returned a malformed control frame." });
  await expectParseFailure(concat(controlFrame({ sample_rate: 24_000 }), controlFrame({ type: "done" })),
    { category: "protocol", message: "The service returned an invalid control frame." });
  for (const rate of [0, -24_000, 24_000.5, "24000", null]) {
    await expectParseFailure(concat(controlFrame({ type: "meta", sample_rate: rate }), controlFrame({ type: "done" })),
      { category: "protocol", message: "The service reported an invalid sample rate." });
  }
});

test("non-finite samples are rejected in any position", async () => {
  for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
    await expectParseFailure(concat(controlFrame({ type: "meta", sample_rate: 24_000 }), pcmFrame([0.1, bad, 0.2]), controlFrame({ type: "done" })),
      { category: "protocol", message: "The service sent a non-finite audio sample." });
  }
});

test("aborting the stream raises AbortError and stops reading", async () => {
  const controller = new AbortController();
  const pending = parseVoxStream(pendingStream(controlFrame({ type: "meta", sample_rate: 24_000 })), controller.signal);
  controller.abort();
  await expect(pending).rejects.toMatchObject({ name: "AbortError" });

  const already = new AbortController();
  already.abort();
  await expect(parseVoxStream(chunkedStream(voxBytes()), already.signal)).rejects.toMatchObject({ name: "AbortError" });
});

test("stream diagnostics stay bounded and describe frames without carrying PCM content", async () => {
  const parsed = await parse(voxBytes([0.5, -0.5]));
  expect(parsed.diagnostic).toContain("frame 1: control meta");
  expect(parsed.diagnostic).toContain("frame 2: pcm · 8 bytes");
  expect(parsed.diagnostic).toContain("frame 3: control done");
  expect(parsed.diagnostic).toContain("total: 3 frames · 8 PCM bytes · 2 samples · 24000 Hz");
  expect(parsed.diagnostic.length).toBeLessThan(2_048);

  const many = await parse(concat(
    controlFrame({ type: "meta", sample_rate: 24_000 }),
    ...Array.from({ length: 4_000 }, () => pcmFrame([0.1, 0.2, 0.3, 0.4])),
    controlFrame({ type: "done" })
  ));
  expect(many.samples.length).toBe(16_000);
  expect(many.diagnostic.length).toBeLessThanOrEqual(64 * 1_024);
});

test("float32 conversion covers negative, zero, and positive boundaries with clamping and little-endian output", () => {
  const bytes = buildPcm16Wav(new Float32Array([0, 1, -1, 0.5, -0.5, 2.5, -2.5, 0.000015]), 24_000);
  const view = new DataView(bytes.buffer, 44);
  // round(value * 32767) is half-up toward +Infinity, so 0.5 and -0.5 are deliberately not symmetric.
  expect([0, 1, 2, 3, 4, 5, 6, 7].map((index) => view.getInt16(index * 2, true)))
    .toEqual([0, 32_767, -32_767, 16_384, -16_383, 32_767, -32_767, 0]);
  // Little-endian: 32767 is 0xff 0x7f in that byte order, never 0x7f 0xff.
  expect([bytes[46], bytes[47]]).toEqual([0xff, 0x7f]);
  expect([Math.round(0.5 * 32_767), Math.round(-0.5 * 32_767)]).toEqual([16_384, -16_383]);
});

test("converted WAV declares correct RIFF sizes, mono PCM16 format, and the reported rate", () => {
  for (const rate of [16_000, 24_000, 44_100]) {
    const bytes = buildPcm16Wav(new Float32Array([0.1, 0.2, 0.3]), rate);
    const parsed = parseWav(bytes);
    if (!parsed.ok) throw new Error(parsed.reason);
    expect(parsed.format).toEqual({ audioFormat: 1, channels: 1, sampleRate: rate, bitsPerSample: 16, dataBytes: 6 });
    const view = new DataView(bytes.buffer);
    expect(bytes.byteLength).toBe(50);
    expect(view.getUint32(4, true)).toBe(42);
    expect(view.getUint32(40, true)).toBe(6);
    expect(view.getUint32(28, true)).toBe(rate * 2);
    expect(view.getUint16(32, true)).toBe(2);
  }
  const empty = parseWav(buildPcm16Wav(new Float32Array(0), 24_000));
  expect(empty.ok).toBe(true);
});

test("the adapter exposes every field, exact default, and group", () => {
  expect(vox.fields.map(({ key, group, initialValue, control }) => [key, group, control, initialValue])).toEqual([
    ["text", "common", "textarea", ""],
    ["reference_audio", "common", "file", null],
    ["control", "common", "text", ""],
    ["cfg_value", "advanced", "number", "2.0"],
    ["inference_timesteps", "advanced", "number", "10"],
    ["min_len", "advanced", "number", "2"],
    ["max_len", "advanced", "number", "4096"],
    ["normalize", "advanced", "checkbox", false],
    ["denoise", "advanced", "checkbox", false],
    ["retry_badcase", "advanced", "checkbox", true],
    ["retry_badcase_max_times", "advanced", "number", "3"],
    ["retry_badcase_ratio_threshold", "advanced", "number", "6.0"]
  ]);
  expect(vox.resultKind).toBe("audio");
  expect(vox.fields.find(({ key }) => key === "reference_audio")?.accept).toBe(".wav,.mp3,.flac,.ogg,.m4a");
  expect(vox.fields.find(({ key }) => key === "control")?.required).toBe(false);
});

test("the strict upload contract blocks unsupported extensions and oversize files", () => {
  for (const name of ["voice.wav", "voice.MP3", "voice.flac", "voice.ogg", "voice.m4a"]) {
    expect(isSupportedVoxUpload(reference(name)), name).toBe(true);
    expect(vox.validate(values({ reference_audio: reference(name) })).errors.reference_audio, name).toBeUndefined();
  }
  // ".wav" alone is a dotfile, which the service's Path(...).suffix treats as having no extension.
  for (const name of ["voice.aac", "voice.txt", "voice", ".wav", "voice.wav.txt"]) {
    expect(isSupportedVoxUpload(reference(name)), name).toBe(false);
    expect(vox.validate(values({ reference_audio: reference(name) })).errors.reference_audio, name).toBe("Choose a .wav, .mp3, .flac, .ogg, .m4a file.");
  }
  expect(vox.validate(values({ reference_audio: null })).errors.reference_audio).toBe("Choose a reference audio file.");
  expect(vox.validate(values({ reference_audio: new File([], "empty.wav", { type: "audio/wav" }) })).errors.reference_audio).toBe("Choose a reference audio file.");

  const oversize = { size: VOX_UPLOAD_LIMIT_BYTES + 1, name: "big.wav", type: "audio/wav" } as unknown as File;
  Object.setPrototypeOf(oversize, File.prototype);
  expect(VOX_UPLOAD_LIMIT_BYTES).toBe(52_428_800);
  expect(vox.validate(values({ reference_audio: oversize })).errors.reference_audio).toBe("Choose a reference audio file of 50 MiB or less.");
});

test("numeric and cross-field rules block only known-invalid values", () => {
  expect(vox.validate(values()).errors.text).toBeUndefined();
  expect(vox.validate(values({ text: "  " })).errors.text).toBe("Enter text to speak.");
  expect(vox.validate(values({ cfg_value: "abc" })).errors.cfg_value).toBe("Enter a finite number.");
  expect(vox.validate(values({ inference_timesteps: "2.5" })).errors.inference_timesteps).toBe("Enter a whole number.");
  expect(vox.validate(values({ retry_badcase_max_times: "x" })).errors.retry_badcase_max_times).toBe("Enter a whole number.");
  expect(vox.validate(values({ cfg_value: "-3" })).errors.cfg_value).toBeUndefined();
  expect(vox.validate(values({ min_len: "5000", max_len: "4096" })).errors.min_len).toBe("The minimum length cannot exceed the maximum length.");
  expect(vox.validate(values({ min_len: "4096", max_len: "4096" })).errors.min_len).toBeUndefined();
  expect(vox.validate(values({ min_len: "", max_len: "" })).errors.min_len).toBeUndefined();
  expect(vox.validate(values()).warnings).toEqual({});
});

interface Captured { readonly url: string; readonly method: string; readonly body: unknown }

async function capture(formValues: FormValues, options: { readonly upload?: () => Response; readonly stream?: () => Response } = {}): Promise<{ readonly calls: readonly Captured[]; readonly execution: Awaited<ReturnType<typeof vox.execute>>; readonly events: readonly ProgressEvent[] }> {
  const calls: Captured[] = [];
  const events: ProgressEvent[] = [];
  const execution = await vox.execute(formValues, new AbortController().signal, (event) => events.push(event), async (input, init) => {
    const url = String(input);
    const body = init?.body;
    calls.push({ url, method: init?.method ?? "GET", body: body instanceof FormData ? [...body.entries()] : typeof body === "string" ? JSON.parse(body) : body });
    if (url.endsWith("/upload-audio")) return (options.upload ?? uploadResponse)();
    return (options.stream ?? (() => voxStreamResponse(voxBytes())))();
  });
  return { calls, execution, events };
}

test("a run uploads the reference then sends every prefilled control on the exact generation shape", async () => {
  const { calls, execution } = await capture(values({ text: " Speak this. ", control: "whispering" }));
  expect(calls.map(({ url, method }) => `${method} ${url}`)).toEqual([
    "POST /proxy/voxcpm2/upload-audio", "POST /proxy/voxcpm2/api/stream"
  ]);
  const upload = calls[0]?.body as ReadonlyArray<readonly [string, File]>;
  expect(upload.map(([name]) => name)).toEqual(["file"]);
  expect(upload[0]?.[1].name).toBe("voice.wav");
  expect(new Uint8Array(await upload[0]![1].arrayBuffer())).toEqual(buildWav());
  expect(calls[1]?.body).toEqual({
    text: " Speak this. ", control: "whispering", cfg_value: 2, inference_timesteps: 10, min_len: 2, max_len: 4_096,
    normalize: false, denoise: false, retry_badcase: true, retry_badcase_max_times: 3, retry_badcase_ratio_threshold: 6,
    reference_wav_path: "fixture-file-id"
  });
  expect(execution.result).toMatchObject({ kind: "audio", mediaType: "audio/wav", sampleRate: 24_000 });
});

test("blank optional control is omitted and checkboxes are always sent explicitly", async () => {
  const cleared = await capture(values({ control: "   ", cfg_value: "", min_len: "", normalize: true, retry_badcase: false }));
  const body = cleared.calls[1]?.body as Record<string, unknown>;
  expect(Object.hasOwn(body, "control")).toBe(false);
  expect(Object.hasOwn(body, "cfg_value")).toBe(false);
  expect(Object.hasOwn(body, "min_len")).toBe(false);
  expect(body).toMatchObject({ normalize: true, denoise: false, retry_badcase: false });
});

test("every run uploads afresh and no file identifier is cached between runs", async () => {
  const ids = ["first-id", "second-id"];
  const seen: string[] = [];
  for (const id of ids) {
    const { calls } = await capture(values(), { upload: () => uploadResponse(id) });
    expect(calls.filter(({ url }) => url.endsWith("/upload-audio"))).toHaveLength(1);
    seen.push((calls[1]?.body as { reference_wav_path: string }).reference_wav_path);
  }
  expect(seen).toEqual(ids);
});

test("phases report upload, generate, and convert without inventing percentages", async () => {
  const { events } = await capture(values());
  expect(events.map((event) => event.kind === "phase" ? `${event.phase}:${event.status}` : event.kind)).toEqual([
    "upload:started", "upload:completed", "generate:started", "generate:completed", "convert:started", "convert:completed"
  ]);
  expect(events.every((event) => !("percent" in event))).toBe(true);
});

test("a successful result is a validated WAV blob with a safe UTC filename", async () => {
  const { execution } = await capture(values({ text: "secret prompt must not name the file" }), { stream: () => voxStreamResponse(voxBytes([0.5, -0.5, 1, -1], 16_000)) });
  if (execution.result.kind !== "audio") throw new Error("Expected an audio result.");
  expect(execution.result.filename).toMatch(/^read2me-voxcpm2-\d{8}T\d{6}Z\.wav$/u);
  expect(execution.result.sampleRate).toBe(16_000);
  expect(execution.result.blob.type).toBe("audio/wav");
  const bytes = new Uint8Array(await execution.result.blob.arrayBuffer());
  expect(bytes.byteLength).toBe(44 + 8);
  const parsed = parseWav(bytes);
  if (!parsed.ok) throw new Error(parsed.reason);
  expect(parsed.format.sampleRate).toBe(16_000);
  expect(new DataView(bytes.buffer, 44).getInt16(2, true)).toBe(-16_383);
});

test("upload failures are mapped and never reach the generation request", async () => {
  const failing = async (upload: () => Response): Promise<unknown> => capture(values(), { upload });
  await expect(failing(() => new Response(JSON.stringify({ detail: "unsupported audio format" }), { status: 400, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "http", status: 400, serviceMessage: "unsupported audio format" });
  await expect(failing(() => new Response(JSON.stringify({ detail: "file too large" }), { status: 413, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "http", status: 413 });
  await expect(failing(() => new Response(JSON.stringify({ kind: "proxy-unavailable", service: "voxcpm2", message: "connect ECONNREFUSED" }), { status: 502, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "unavailable", status: 502 });
  await expect(failing(() => new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "protocol", message: "The upload response did not contain a file identifier." });

  let generationCalled = false;
  await expect(vox.execute(values(), new AbortController().signal, noProgress, async (input) => {
    if (String(input).endsWith("/api/stream")) { generationCalled = true; return voxStreamResponse(voxBytes()); }
    return new Response("nope", { status: 500, headers: { "content-type": "text/plain" } });
  })).rejects.toMatchObject({ category: "http", status: 500 });
  expect(generationCalled).toBe(false);
});

test("generation failures map to reached, protocol, network, and cancellation outcomes", async () => {
  const run = async (stream: () => Response): Promise<unknown> => capture(values(), { stream });
  await expect(run(() => new Response(JSON.stringify({ detail: "invalid request" }), { status: 422, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "http", status: 422, serviceMessage: "invalid request" });
  await expect(run(() => new Response(JSON.stringify({ kind: "proxy-unavailable", service: "voxcpm2", message: "down" }), { status: 502, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "unavailable", status: 502 });
  await expect(run(() => new Response(JSON.stringify({ ok: true }), { status: 200, headers: { "content-type": "application/json" } })))
    .rejects.toMatchObject({ category: "protocol", message: "The service returned a success response that is not a framed audio stream." });
  await expect(run(() => voxStreamResponse(voxBytes().slice(0, 12))))
    .rejects.toMatchObject({ category: "protocol", message: "The service response ended before completion." });
  await expect(run(() => voxStreamResponse(controlFrame({ type: "error", message: "model not loaded" }))))
    .rejects.toMatchObject({ category: "http", serviceMessage: "model not loaded" });

  await expect(vox.execute(values(), new AbortController().signal, noProgress, async (input) => {
    if (String(input).endsWith("/upload-audio")) return uploadResponse();
    throw new TypeError("Failed to fetch");
  })).rejects.toMatchObject({ category: "unavailable", message: `The dashboard could not reach ${vox.name} through the local proxy.` });

  const controller = new AbortController();
  let observed = false;
  // Cancel once the generation request is genuinely in flight, rather than racing the upload's awaits.
  const pending = vox.execute(values(), controller.signal, noProgress, async (input, init) => {
    if (String(input).endsWith("/upload-audio")) return uploadResponse();
    return new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => { observed = true; reject(new DOMException("aborted", "AbortError")); }, { once: true });
      controller.abort();
    });
  });
  await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  expect(observed).toBe(true);
});

test("a failed run exposes no audio, and diagnostics carry frames and headers but no PCM bytes", async () => {
  const failure = await capture(values(), { stream: () => voxStreamResponse(concat(controlFrame({ type: "meta", sample_rate: 24_000 }), pcmFrame([0.5, 0.6]))) })
    .then(() => undefined, (error: unknown) => error);
  expect(failure).toMatchObject({ category: "protocol", partialResult: undefined });
  expect((failure as { diagnostic: string }).diagnostic).toContain("frame 2: pcm · 8 bytes");
  expect((failure as { diagnostic: string }).diagnostic).toContain("[incomplete]");

  const { execution } = await capture(values({ text: "diagnostics" }), { stream: () => voxStreamResponse(voxBytes([0.5, -0.5])) });
  expect(execution.diagnostic).toContain("POST /proxy/voxcpm2/upload-audio");
  expect(execution.diagnostic).toContain("POST /proxy/voxcpm2/api/stream");
  expect(execution.diagnostic).toContain("Content-Type: application/octet-stream");
  expect(execution.diagnostic).toContain("total: 3 frames · 8 PCM bytes");
  expect(execution.diagnostic).toContain("WAV format 1 · 1 channel · 24000 Hz · 16-bit · 4 data bytes");
  expect(execution.diagnostic).not.toContain("RIFF");
  expect(execution.diagnostic.length).toBeLessThan(4_096);
});

test("bounded diagnostics survive an oversize upload error body", async () => {
  await expect(capture(values(), { upload: () => new Response(`{"detail":"${"x".repeat(70 * 1_024)}"}`, { status: 500, headers: { "content-type": "application/json" } }) }))
    .rejects.toMatchObject({ category: "http", status: 500, diagnostic: /\[truncated\]/u });
});

test("input summaries reduce the reference file to metadata without retaining bytes", () => {
  const summary = vox.summarizeInput(values({ text: "x".repeat(5_000), control: "calm", normalize: true }));
  expect(summary[0]?.value).toHaveLength(4_096);
  expect(summary[1]).toEqual({ label: "Reference audio", value: `voice.wav · ${buildWav().byteLength} bytes · audio/wav` });
  expect(summary.find((item) => item.label === "Normalize text")?.value).toBe("Yes");
  expect(summary.find((item) => item.label === "Retry bad cases")?.value).toBe("Yes");
  expect(summary.some((item) => item.value instanceof File)).toBe(false);
});
