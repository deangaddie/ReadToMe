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
  response.writeHead(200, { "content-type": "application/json" });
  response.end(JSON.stringify(readinessPayload(service)));
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
  if (url.pathname === "/readiness-hold") {
    heldReadinessService = url.searchParams.get("service") ?? undefined;
    response.setHeader("content-type", "application/json");
    response.end(JSON.stringify({ held: heldReadinessService }));
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
