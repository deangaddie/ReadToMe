import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { once } from "node:events";
import { resolve } from "node:path";

const services = [
  ["LLAMA", "llama"],
  ["CHATTERBOX", "chatterbox"],
  ["CHATTERBOX_TURBO", "chatterbox-turbo"],
  ["QWEN3_TTS", "qwen3-tts"],
  ["QWEN3_TTS_BASE", "qwen3-tts-base"],
  ["VOXCPM2", "voxcpm2"],
  ["WHISPER", "whisper"],
  ["MINILM_L6", "minilm-l6"],
  ["MPNET_BASE_V2", "mpnet-base-v2"]
];

const servers = [];
const serviceServers = new Map();
const env = { ...process.env, CHD_NO_OPEN: "1" };
let abortObserved = false;
const abortWaiters = [];
const readinessStarts = [];
const readinessCompletions = [];
const pendingReadiness = new Map();
let readinessBatchStarts = new Set();
let releasingRemainder = false;
let heldReadinessService;
const readinessOverrides = new Map();
let llamaMode = "success";
let llamaModelCalls = 0;
const llamaRequests = [];

function readinessPayload(service) {
  if (service === "llama" || service === "whisper") return { status: "ok" };
  if (service === "voxcpm2") return { status: "ok", model_loaded: true };
  if (service === "minilm-l6" || service === "mpnet-base-v2") return { openapi: "3.1.0", info: { title: service, version: "1.0.0" } };
  if (service.startsWith("qwen3")) return { status: "ok", device: "cuda", model: service };
  return { status: "ok", device: "cuda" };
}

function nextPendingInReverseOrder() {
  return services.map(([, service]) => service).reverse().find((service) => pendingReadiness.has(service) && service !== heldReadinessService);
}

function releaseOneReadiness() {
  const service = nextPendingInReverseOrder();
  if (service === undefined) return;
  const response = pendingReadiness.get(service);
  pendingReadiness.delete(service);
  readinessCompletions.push(service);
  const override = readinessOverrides.get(service);
  response.writeHead(override === "error" ? 500 : 200, { "content-type": "application/json" });
  response.end(JSON.stringify(override === "error" ? { detail: "fixture readiness error" } : readinessPayload(service)));
}

function releaseReadinessRemainder() {
  if (releasingRemainder) return;
  releasingRemainder = true;
  const releaseNext = () => {
    if (pendingReadiness.size === 0) {
      readinessBatchStarts = new Set();
      releasingRemainder = false;
      return;
    }
    if (nextPendingInReverseOrder() === undefined) {
      releasingRemainder = false;
      return;
    }
    releaseOneReadiness();
    setImmediate(releaseNext);
  };
  setImmediate(releaseNext);
}

function observeAbort() {
  abortObserved = true;
  for (const response of abortWaiters.splice(0)) {
    response.end(JSON.stringify({ abortObserved: true }));
  }
}

function readBody(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => resolve(Buffer.concat(chunks)));
    request.on("error", reject);
  });
}

function speechWavFixture(sampleRate = 24_000, seconds = 0.5) {
  const samples = Math.round(sampleRate * seconds);
  const dataBytes = samples * 2;
  const buffer = Buffer.alloc(44 + dataBytes);
  buffer.write("RIFF", 0);
  buffer.writeUInt32LE(36 + dataBytes, 4);
  buffer.write("WAVEfmt ", 8);
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(sampleRate * 2, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write("data", 36);
  buffer.writeUInt32LE(dataBytes, 40);
  for (let index = 0; index < samples; index += 1) {
    buffer.writeInt16LE(Math.round(Math.sin((index / sampleRate) * 2 * Math.PI * 440) * 8_000), 44 + index * 2);
  }
  return buffer;
}

const ttsRequests = [];

function ttsRoute(service, pathname) {
  if (service === "chatterbox-turbo") return pathname === "/tts/turbo";
  return pathname === "/tts" && (service === "chatterbox" || service === "qwen3-tts" || service === "qwen3-tts-base");
}

async function handleTts(service, request, response) {
  const body = await readBody(request);
  const contentType = request.headers["content-type"] ?? "";
  ttsRequests.push({ service, contentType, body: body.toString("base64") });
  const text = body.toString("latin1");
  if (text.includes("fixture-http-error")) {
    response.writeHead(422, { "content-type": "application/json" });
    response.end(JSON.stringify({ detail: "fixture rejected the speech request" }));
    return;
  }
  if (text.includes("fixture-wrong-media")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ audio: "not really" }));
    return;
  }
  if (text.includes("fixture-malformed-wav")) {
    response.writeHead(200, { "content-type": "audio/wav" });
    // A RIFF header declaring a zero size, with no fmt or data chunk.
    response.end(Buffer.concat([Buffer.from("RIFF", "latin1"), Buffer.alloc(4), Buffer.from("WAVEnope", "latin1")]));
    return;
  }
  if (text.includes("fixture-truncated-wav")) {
    response.writeHead(200, { "content-type": "audio/wav" });
    response.end(speechWavFixture().subarray(0, 200));
    return;
  }
  if (text.includes("fixture-slow")) {
    request.on("aborted", observeAbort);
    response.on("close", () => { if (!response.writableEnded) observeAbort(); });
    const timer = setTimeout(() => {
      if (!response.destroyed) {
        response.writeHead(200, { "content-type": "audio/wav" });
        response.end(speechWavFixture());
      }
    }, 10_000);
    response.on("close", () => clearTimeout(timer));
    return;
  }
  response.writeHead(200, { "content-type": "audio/wav" });
  response.end(speechWavFixture());
}

const whisperRequests = [];

/**
 * Reads one multipart field value, or a file part's filename, without pulling in a parser dependency.
 * Only real `Header: value` lines may precede the blank line, so the match cannot run past this part.
 */
function multipartField(text, name) {
  const match = new RegExp(`name="${name}"([^\\r\\n]*)\\r\\n(?:[A-Za-z][A-Za-z-]*:[^\\r\\n]*\\r\\n)*\\r\\n([\\s\\S]*?)\\r\\n--`, "u").exec(text);
  if (match === null) return undefined;
  const filename = /filename="([^"]*)"/u.exec(match[1] ?? "");
  return filename === null ? match[2] : filename[1];
}

const WHISPER_TRANSCRIPT = " It was a bright cold day in April.";
const WHISPER_VERBOSE = {
  task: "transcribe",
  language: "en",
  duration: 2.4,
  text: WHISPER_TRANSCRIPT,
  segments: [
    {
      id: 0, start: 0, end: 1.1, text: " It was a bright",
      words: [
        { word: " It", start: 0, end: 0.2, probability: 0.98 },
        { word: " was", start: 0.2, end: 0.5, probability: 0.94 },
        { word: " a", start: 0.5, end: 0.7, probability: 0.87 },
        { word: " bright", start: 0.7, end: 1.1, probability: 0.96 }
      ]
    },
    {
      id: 1, start: 1.1, end: 2.4, text: " cold day in April.",
      words: [
        { word: " cold", start: 1.1, end: 1.5, probability: 0.93 },
        { word: " day", start: 1.5, end: 1.8, probability: 0.97 },
        { word: " in", start: 1.8, end: 2.0, probability: 0.9 },
        { word: " April.", start: 2.0, end: 2.4, probability: 0.99 }
      ]
    }
  ]
};
const WHISPER_SRT = "1\r\n00:00:00,000 --> 00:00:01,100\r\n It was a bright\r\n\r\n2\r\n00:00:01,100 --> 00:00:02,400\r\n cold day in April.\r\n\r\n";
const WHISPER_VTT = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.100\r\n It was a bright\r\n\r\n00:00:01.100 --> 00:00:02.400\r\n cold day in April.\r\n\r\n";

async function handleWhisper(request, response) {
  const body = await readBody(request);
  const text = body.toString("latin1");
  const contentType = request.headers["content-type"] ?? "";
  const fields = {};
  for (const name of [
    "file", "response_format", "language", "token_timestamps", "max_len", "split_on_word", "no_timestamps",
    "prompt", "vad", "translate", "detect_language", "best_of", "temperature", "beam_size", "offset_t", "duration"
  ]) {
    const value = multipartField(text, name);
    if (value !== undefined) fields[name] = value;
  }
  whisperRequests.push({ contentType, fields, hasPrompt: text.includes('name="prompt"'), bytes: body.length });
  const filename = fields.file ?? "";
  const format = fields.response_format ?? "json";

  if (filename.includes("fixture-http-error")) {
    response.writeHead(400, { "content-type": "application/json" });
    response.end(JSON.stringify({ error: "failed to read the audio file" }));
    return;
  }
  if (filename.includes("fixture-malformed")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end('{"text": "unterminated');
    return;
  }
  if (filename.includes("fixture-wrong-media")) {
    response.writeHead(200, { "content-type": "text/html" });
    response.end(JSON.stringify(WHISPER_VERBOSE));
    return;
  }
  if (filename.includes("fixture-reversed")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ text: "reversed", segments: [{ start: 2, end: 1, text: "reversed", words: [] }] }));
    return;
  }
  if (filename.includes("fixture-no-words")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ ...WHISPER_VERBOSE, segments: WHISPER_VERBOSE.segments.map(({ words, ...rest }) => rest) }));
    return;
  }
  if (filename.includes("fixture-slow")) {
    request.on("aborted", observeAbort);
    response.on("close", () => { if (!response.writableEnded) observeAbort(); });
    const timer = setTimeout(() => {
      if (!response.destroyed) {
        response.writeHead(200, { "content-type": "application/json" });
        response.end(JSON.stringify(WHISPER_VERBOSE));
      }
    }, 10_000);
    response.on("close", () => clearTimeout(timer));
    return;
  }
  if (format === "text" || format === "srt" || format === "vtt") {
    response.writeHead(200, { "content-type": "text/plain" });
    response.end(format === "text" ? `${WHISPER_TRANSCRIPT}\n` : format === "srt" ? WHISPER_SRT : WHISPER_VTT);
    return;
  }
  response.writeHead(200, { "content-type": "application/json" });
  response.end(JSON.stringify(format === "verbose_json" ? WHISPER_VERBOSE : { text: WHISPER_TRANSCRIPT }));
}

const voxUploads = [];
const voxRequests = [];
let voxUploadCount = 0;

function voxFrame(type, payload) {
  const header = Buffer.alloc(5);
  header.writeUInt8(type, 0);
  header.writeUInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

function voxControlFrame(value) {
  return voxFrame(0, Buffer.from(JSON.stringify(value), "utf8"));
}

function voxPcmFrame(count, offset) {
  const payload = Buffer.alloc(count * 4);
  for (let index = 0; index < count; index += 1) {
    payload.writeFloatLE(Math.sin(((index + offset) / 24_000) * 2 * Math.PI * 440) * 0.5, index * 4);
  }
  return voxFrame(1, payload);
}

/** Writes bytes in deliberately awkward pieces that never align with frame boundaries. */
function writeDelayed(response, bytes, boundaries) {
  const offsets = [...new Set([0, ...boundaries, bytes.length])].sort((a, b) => a - b);
  let index = 0;
  const writeNext = () => {
    if (response.destroyed) return;
    if (index >= offsets.length - 1) { response.end(); return; }
    response.write(bytes.subarray(offsets[index], offsets[index + 1]));
    index += 1;
    setImmediate(writeNext);
  };
  setImmediate(writeNext);
}

async function handleVoxUpload(request, response) {
  const body = await readBody(request);
  const contentType = request.headers["content-type"] ?? "";
  const text = body.toString("latin1");
  voxUploads.push({ contentType, bytes: body.length, body: body.toString("base64") });
  if (text.includes("fixture-upload-error")) {
    response.writeHead(400, { "content-type": "application/json" });
    response.end(JSON.stringify({ detail: "unsupported audio format" }));
    return;
  }
  voxUploadCount += 1;
  response.writeHead(200, { "content-type": "application/json" });
  response.end(JSON.stringify({ file_id: `vox-file-${voxUploadCount}` }));
}

async function handleVoxStream(request, response) {
  const body = await readBody(request);
  let payload;
  try { payload = JSON.parse(body.toString("utf8")); } catch { payload = undefined; }
  voxRequests.push(payload);
  const text = typeof payload?.text === "string" ? payload.text : "";
  if (text.includes("fixture-http-error")) {
    response.writeHead(422, { "content-type": "application/json" });
    response.end(JSON.stringify({ detail: "invalid request: text is required" }));
    return;
  }
  if (text.includes("fixture-wrong-media")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ audio: "not framed" }));
    return;
  }
  response.writeHead(200, { "content-type": "application/octet-stream" });
  if (text.includes("fixture-framed-error")) {
    response.end(voxControlFrame({ type: "error", message: "model not loaded" }));
    return;
  }
  if (text.includes("fixture-protocol")) {
    // Meta and audio, but the stream ends without the required done frame.
    response.end(Buffer.concat([voxControlFrame({ type: "meta", sample_rate: 24_000 }), voxPcmFrame(16, 0)]));
    return;
  }
  if (text.includes("fixture-slow")) {
    request.on("aborted", observeAbort);
    response.on("close", () => { if (!response.writableEnded) observeAbort(); });
    response.write(voxControlFrame({ type: "meta", sample_rate: 24_000 }));
    const timer = setTimeout(() => {
      if (!response.destroyed) response.end(Buffer.concat([voxPcmFrame(16, 0), voxControlFrame({ type: "done", chunks: 1 })]));
    }, 10_000);
    response.on("close", () => clearTimeout(timer));
    return;
  }
  const bytes = Buffer.concat([
    voxControlFrame({ type: "meta", sample_rate: 24_000 }),
    voxPcmFrame(1_200, 0), voxPcmFrame(1_200, 1_200), voxPcmFrame(1_200, 2_400),
    voxControlFrame({ type: "done", chunks: 3 })
  ]);
  // Split mid-header and mid-payload so the client must buffer across arbitrary boundaries.
  writeDelayed(response, bytes, [3, 7, 40, 41, 1_000, bytes.length - 9, bytes.length - 2]);
}

function wavFixture() {
  const buffer = Buffer.alloc(48);
  buffer.write("RIFF", 0);
  buffer.writeUInt32LE(40, 4);
  buffer.write("WAVEfmt ", 8);
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(24_000, 24);
  buffer.writeUInt32LE(48_000, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write("data", 36);
  buffer.writeUInt32LE(4, 40);
  buffer.writeInt16LE(-1234, 44);
  buffer.writeInt16LE(2345, 46);
  return buffer;
}

async function handle(service, request, response, server) {
  const url = new URL(request.url ?? "/", "http://fixture");
  if (url.pathname === "/llama-mode") {
    llamaMode = url.searchParams.get("mode") ?? "success";
    if (llamaMode === "success") { llamaModelCalls = 0; llamaRequests.splice(0); abortObserved = false; }
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ mode: llamaMode }));
    return;
  }
  if (url.pathname === "/llama-events") {
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ modelCalls: llamaModelCalls, requests: llamaRequests }));
    return;
  }
  if (url.pathname === "/tts-events") {
    if (url.searchParams.get("reset") === "1") ttsRequests.splice(0);
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ requests: ttsRequests }));
    return;
  }
  if (url.pathname === "/whisper-events") {
    if (url.searchParams.get("reset") === "1") { whisperRequests.splice(0); abortObserved = false; }
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ requests: whisperRequests }));
    return;
  }
  if (url.pathname === "/vox-events") {
    if (url.searchParams.get("reset") === "1") { voxUploads.splice(0); voxRequests.splice(0); voxUploadCount = 0; abortObserved = false; }
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ uploads: voxUploads, requests: voxRequests }));
    return;
  }
  if (url.pathname === "/readiness-events") {
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ starts: readinessStarts, completions: readinessCompletions, count: readinessStarts.filter((item) => item === "llama").length }));
    return;
  }
  if (url.pathname === "/restart-service") {
    const requested = url.searchParams.get("service");
    const entry = serviceServers.get(requested);
    if (entry && !entry.server.listening) {
      entry.server.listen(entry.port, "127.0.0.1");
      await once(entry.server, "listening");
    }
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ service: requested, listening: entry?.server.listening ?? false }));
    return;
  }
  if (url.pathname === "/shutdown-service") {
    const requested = url.searchParams.get("service");
    const entry = serviceServers.get(requested);
    const wasListening = entry?.server.listening ?? false;
    if (entry?.server.listening) await new Promise((resolve) => entry.server.close(resolve));
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ service: requested, stopped: wasListening }));
    return;
  }
  if (url.pathname === "/readiness-hold") {
    heldReadinessService = url.searchParams.get("service") ?? undefined;
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ held: heldReadinessService }));
    return;
  }
  if (url.pathname === "/readiness-state") {
    const requested = url.searchParams.get("service");
    const state = url.searchParams.get("state");
    if (state === "ready") readinessOverrides.delete(requested);
    else readinessOverrides.set(requested, state);
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ service: requested, state }));
    return;
  }
  if (url.pathname === "/readiness-release") {
    heldReadinessService = undefined;
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ released: true }));
    releaseReadinessRemainder();
    return;
  }
  if (url.pathname === "/health" || url.pathname === "/openapi.json") {
    readinessStarts.push(service);
    readinessBatchStarts.add(service);
    pendingReadiness.set(service, response);
    if (readinessBatchStarts.size === services.length) releaseReadinessRemainder();
    else if (pendingReadiness.size >= 6) releaseOneReadiness();
    return;
  }
  if (ttsRoute(service, url.pathname)) {
    await handleTts(service, request, response);
    return;
  }
  if (service === "whisper" && url.pathname === "/inference") {
    await handleWhisper(request, response);
    return;
  }
  if (service === "voxcpm2" && url.pathname === "/upload-audio") {
    await handleVoxUpload(request, response);
    return;
  }
  if (service === "voxcpm2" && url.pathname === "/api/stream") {
    await handleVoxStream(request, response);
    return;
  }
  if (service === "llama" && url.pathname === "/v1/models") {
    llamaModelCalls += 1;
    if (llamaMode === "models-failure") {
      response.writeHead(503, { "content-type": "application/json" });
      response.end(JSON.stringify({ error: { message: "model router unavailable" } }));
      return;
    }
    if (llamaMode === "models-slow") {
      setTimeout(() => {
        response.writeHead(200, { "content-type": "application/json" });
        response.end(JSON.stringify({ object: "list", data: [
          { id: "gemma-sleeping", status: { value: "sleeping", preset: true } },
          { id: "gemma-loaded", status: { value: "loaded", preset: true } }
        ] }));
      }, 250);
      return;
    }
    response.writeHead(200, { "content-type": "application/json" });
    if (llamaMode === "fallback") {
      response.end(JSON.stringify({ object: "list", data: [{ id: "gemma-fallback", status: { value: "unloaded", preset: true } }] }));
      return;
    }
    response.end(JSON.stringify({ object: "list", data: [
      { id: "gemma-sleeping", status: { value: "sleeping", preset: true } },
      { id: "gemma-loaded", status: { value: "loaded", preset: true } },
      { id: "gemma-failed", status: { value: "unloaded", preset: true, failed: true, last_error: "fixture failure" } }
    ] }));
    return;
  }
  if (service === "llama" && url.pathname === "/v1/chat/completions") {
    const body = await readBody(request);
    let payload;
    try { payload = JSON.parse(body.toString("utf8")); } catch { payload = undefined; }
    llamaRequests.push(payload);
    if (llamaMode === "disconnect") {
      response.destroy();
      return;
    }
    response.writeHead(200, { "content-type": "text/event-stream" });
    if (llamaMode === "incomplete") {
      response.end('data: {"choices":[{"delta":{"content":"partial answer"}}]}\n\n');
      return;
    }
    if (llamaMode === "slow") {
      request.on("aborted", observeAbort);
      response.on("close", () => { if (!response.writableEnded) observeAbort(); });
      response.write('data: {"choices":[{"delta":{"reasoning_content":"partial thought"}}]}\n\n');
      const timer = setTimeout(() => response.end('data: {"choices":[{"delta":{"content":"late answer"}}]}\n\ndata: [DONE]\n\n'), 10_000);
      response.on("close", () => clearTimeout(timer));
      return;
    }
    const streamed = Buffer.from('data: {"choices":[{"delta":{"reasoning_content":"think 💡"}}]}\r\n\r\n' +
      'data: {"choices":[{"delta":{"content":"streamed answer"}}]}\n\n' +
      'data: {"choices":[{"delta":{},"finish_reason":"stop","index":0}],"timings":{"predicted_ms":8}}\n\n' +
      'data: [DONE]\n\n', "utf8");
    response.write(streamed.subarray(0, 17));
    setImmediate(() => { response.write(streamed.subarray(17, 63)); setImmediate(() => response.end(streamed.subarray(63))); });
    return;
  }
  if (url.pathname === "/similarity" && (service === "minilm-l6" || service === "mpnet-base-v2")) {
    const body = await readBody(request);
    let payload;
    try { payload = JSON.parse(body.toString("utf8")); } catch { payload = undefined; }
    if (!payload || Object.keys(payload).sort().join(",") !== "text1,text2" || typeof payload.text1 !== "string" || typeof payload.text2 !== "string") {
      response.writeHead(400, { "content-type": "application/json" });
      response.end(JSON.stringify({ detail: "request body did not match the similarity contract" }));
      return;
    }
    if (payload.text1 === "http-error") {
      response.writeHead(422, { "content-type": "application/json" });
      response.end(JSON.stringify({ detail: "fixture rejected input" }));
      return;
    }
    if (payload.text1 === "malformed") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end('{"similarity":"not-a-number"}');
      return;
    }
    if (payload.text1 === "slow") {
      request.on("aborted", observeAbort);
      response.on("close", () => { if (!response.writableEnded) observeAbort(); });
      const timer = setTimeout(() => {
        if (!response.destroyed) {
          response.writeHead(200, { "content-type": "application/json" });
          response.end(JSON.stringify({ similarity: 0.5 }));
        }
      }, 10_000);
      response.on("close", () => clearTimeout(timer));
      return;
    }
    const similarity = payload.text1 === "negative" ? -0.25 : payload.text1 === "outside" ? 1.25 : 0.987654321;
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ similarity }));
    return;
  }
  if (url.pathname === "/echo") {
    const body = await readBody(request);
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ service, method: request.method, path: url.pathname + url.search, body: body.toString("base64"), contentType: request.headers["content-type"] ?? "" }));
    return;
  }
  if (url.pathname === "/sse") {
    response.writeHead(200, { "content-type": "text/event-stream", "x-fixture": "sse" });
    response.write("data: {\"part\":\"one\"}\n\n");
    setImmediate(() => response.end("data: [DONE]\n\n"));
    return;
  }
  if (url.pathname === "/framed") {
    response.writeHead(200, { "content-type": "application/octet-stream" });
    response.end(Buffer.from([0, 3, 0, 0, 0, 97, 98, 99, 1, 2, 0, 0, 0, 0, 255]));
    return;
  }
  if (url.pathname === "/wav") {
    response.writeHead(200, { "content-type": "audio/wav", "x-fixture": "wav" });
    response.end(wavFixture());
    return;
  }
  if (url.pathname === "/error") {
    response.writeHead(418, { "content-type": "application/problem+json", "x-service-error": service });
    response.end(JSON.stringify({ detail: "reached service" }));
    return;
  }
  if (url.pathname === "/malformed") {
    response.writeHead(200, { "content-type": "application/json" });
    response.end("{not-json");
    return;
  }
  if (url.pathname === "/slow") {
    response.writeHead(200, { "content-type": "application/octet-stream" });
    const chunk = Buffer.alloc(256 * 1024, 0x5a);
    for (let index = 0; index < 32; index += 1) {
      if (!response.write(chunk)) await once(response, "drain");
    }
    response.end();
    return;
  }
  if (url.pathname === "/abort-status") {
    response.setHeader("content-type", "application/json");
    if (abortObserved) response.end(JSON.stringify({ abortObserved: true }));
    else abortWaiters.push(response);
    return;
  }
  if (url.pathname === "/abort") {
    request.on("aborted", observeAbort);
    response.on("close", () => { if (!response.writableEnded) observeAbort(); });
    response.writeHead(200, { "content-type": "application/octet-stream" });
    const timer = setInterval(() => response.write(Buffer.alloc(64 * 1024)), 5);
    response.on("close", () => clearInterval(timer));
    return;
  }
  if (url.pathname === "/shutdown") {
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ stopped: service }));
    setImmediate(() => server.close());
    return;
  }
  response.writeHead(404, { "content-type": "text/plain" });
  response.end("fixture route not found");
}

for (const [envSuffix, service] of services) {
  const server = createServer((request, response) => void handle(service, request, response, server));
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (typeof address === "string" || address === null) throw new Error(`No fixture port for ${service}`);
  env[`CHD_TARGET_${envSuffix}`] = `http://127.0.0.1:${address.port}`;
  servers.push(server);
  serviceServers.set(service, { server, port: address.port });
}

const viteExecutable = resolve("node_modules", "vite", "bin", "vite.js");
const vite = spawn(process.execPath, [viteExecutable], { env, stdio: "inherit", shell: false });

let stopping = false;
async function stop(exitCode) {
  if (stopping) return;
  stopping = true;
  vite.kill();
  await Promise.all(servers.map((server) => new Promise((resolve) => server.close(resolve))));
  process.exit(exitCode);
}

vite.on("exit", (code) => void stop(code ?? 1));
process.on("SIGINT", () => void stop(0));
process.on("SIGTERM", () => void stop(0));
process.on("uncaughtException", (error) => { console.error(error); void stop(1); });
process.on("unhandledRejection", (error) => { console.error(error); void stop(1); });
