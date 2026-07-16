export const READINESS_TIMEOUT_MS = 1_500;
export const READINESS_DIAGNOSTIC_LIMIT = 2_048;
const READINESS_BODY_LIMIT = 64 * 1_024;

export type ServiceId =
  | "llama"
  | "chatterbox"
  | "chatterbox-turbo"
  | "qwen3-tts"
  | "qwen3-tts-base"
  | "voxcpm2"
  | "whisper"
  | "minilm-l6"
  | "mpnet-base-v2";

export type ReadinessState = "Ready" | "Loading" | "Unavailable" | "Error" | "Unknown";
export type ComputeKind = "CPU" | "GPU";

export interface ServiceAdapter {
  readonly id: ServiceId;
  readonly name: string;
  readonly shortName: string;
  readonly purpose: string;
  readonly endpoint: string;
  readonly port: number;
  readonly compute: ComputeKind;
  readonly probePath: "/health" | "/openapi.json";
  validateReadiness(payload: unknown): ReadinessState | undefined;
  checkReadiness(fetcher?: typeof fetch, options?: ProbeOptions): Promise<ReadinessObservation>;
}

export interface ReadinessObservation {
  readonly state: ReadinessState;
  readonly message: string;
  readonly diagnostic: string;
  readonly checkedAt: string;
  readonly latencyMs: number;
}

export interface ReadinessClock {
  now(): number;
  setTimeout(callback: () => void, delay: number): unknown;
  clearTimeout(handle: unknown): void;
}

const browserClock: ReadinessClock = {
  now: () => performance.now(),
  setTimeout: (callback, delay) => globalThis.setTimeout(callback, delay),
  clearTimeout: (handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>)
};

export const SERVICE_ADAPTERS: readonly ServiceAdapter[] = [
  defineServiceAdapter({ id: "llama", name: "Llama router", shortName: "Llama", purpose: "Language model routing and completion", endpoint: "GET /health", port: 8080, compute: "GPU", probePath: "/health", validateReadiness: validateStatusOk }),
  defineServiceAdapter({ id: "chatterbox", name: "Chatterbox TTS", shortName: "Chatterbox", purpose: "Expressive cloned-voice speech", endpoint: "GET /health", port: 8000, compute: "GPU", probePath: "/health", validateReadiness: validateDeviceHealth }),
  defineServiceAdapter({ id: "chatterbox-turbo", name: "Chatterbox Turbo", shortName: "Turbo", purpose: "Tagged paralinguistic speech", endpoint: "GET /health", port: 8001, compute: "GPU", probePath: "/health", validateReadiness: validateDeviceHealth }),
  defineServiceAdapter({ id: "qwen3-tts", name: "Qwen3 Voice Design", shortName: "Qwen Design", purpose: "Speech from a voice description", endpoint: "GET /health", port: 8100, compute: "GPU", probePath: "/health", validateReadiness: validateQwenHealth }),
  defineServiceAdapter({ id: "qwen3-tts-base", name: "Qwen3 TTS Base", shortName: "Qwen Base", purpose: "Cloned-voice speech synthesis", endpoint: "GET /health", port: 8101, compute: "GPU", probePath: "/health", validateReadiness: validateQwenHealth }),
  defineServiceAdapter({ id: "voxcpm2", name: "VoxCPM2", shortName: "VoxCPM2", purpose: "Streaming cloned-voice speech", endpoint: "GET /health", port: 8003, compute: "GPU", probePath: "/health", validateReadiness: validateVoxHealth }),
  defineServiceAdapter({ id: "whisper", name: "Whisper.cpp", shortName: "Whisper", purpose: "Speech transcription and timings", endpoint: "GET /health", port: 9000, compute: "CPU", probePath: "/health", validateReadiness: validateStatusOk }),
  defineServiceAdapter({ id: "minilm-l6", name: "MiniLM-L6", shortName: "MiniLM", purpose: "Fast semantic similarity", endpoint: "GET /openapi.json", port: 8200, compute: "CPU", probePath: "/openapi.json", validateReadiness: validateFastApiOpenApi }),
  defineServiceAdapter({ id: "mpnet-base-v2", name: "MPNet Base v2", shortName: "MPNet", purpose: "High-quality semantic similarity", endpoint: "GET /openapi.json", port: 8201, compute: "CPU", probePath: "/openapi.json", validateReadiness: validateFastApiOpenApi })
] as const;

export interface ProbeOptions {
  readonly clock?: ReadinessClock;
  readonly wallNow?: () => Date;
}

interface BoundedBody {
  readonly text: string;
  readonly truncated: boolean;
}

async function readBoundedBody(response: Response): Promise<BoundedBody> {
  if (response.body === null) return { text: "", truncated: false };
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let text = "";
  let bytes = 0;
  let truncated = false;
  while (true) {
    const item = await reader.read();
    if (item.done) break;
    const remaining = READINESS_BODY_LIMIT - bytes;
    if (item.value.byteLength > remaining) {
      text += decoder.decode(item.value.subarray(0, Math.max(0, remaining)), { stream: true });
      truncated = true;
      await reader.cancel();
      break;
    }
    bytes += item.value.byteLength;
    text += decoder.decode(item.value, { stream: true });
  }
  text += decoder.decode();
  return { text, truncated };
}

function boundDiagnostic(text: string, wasTruncated = false): string {
  const suffix = "\n[truncated]";
  if (!wasTruncated && text.length <= READINESS_DIAGNOSTIC_LIMIT) return text;
  return `${text.slice(0, READINESS_DIAGNOSTIC_LIMIT)}${suffix}`;
}

function isJsonMediaType(value: string | null): boolean {
  const mediaType = value?.split(";", 1)[0]?.trim().toLowerCase() ?? "";
  return mediaType === "application/json" || mediaType.endsWith("+json");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function validateStatusOk(payload: unknown): ReadinessState | undefined {
  return isRecord(payload) && payload.status === "ok" ? "Ready" : undefined;
}

function validateDeviceHealth(payload: unknown): ReadinessState | undefined {
  return isRecord(payload) && payload.status === "ok" && nonEmptyString(payload.device) ? "Ready" : undefined;
}

function validateQwenHealth(payload: unknown): ReadinessState | undefined {
  return isRecord(payload) && payload.status === "ok" && nonEmptyString(payload.device) && nonEmptyString(payload.model) ? "Ready" : undefined;
}

function validateVoxHealth(payload: unknown): ReadinessState | undefined {
  if (!isRecord(payload) || payload.status !== "ok") return undefined;
  if (payload.model_loaded === false) return "Loading";
  return payload.model_loaded === true ? "Ready" : undefined;
}

function validateFastApiOpenApi(payload: unknown): ReadinessState | undefined {
  if (!isRecord(payload) || !/^3\.\d+\.\d+$/u.test(typeof payload.openapi === "string" ? payload.openapi : "") || !isRecord(payload.info)) return undefined;
  return nonEmptyString(payload.info.title) && nonEmptyString(payload.info.version) ? "Ready" : undefined;
}

function defineServiceAdapter(definition: Omit<ServiceAdapter, "checkReadiness">): ServiceAdapter {
  const adapter: ServiceAdapter = {
    ...definition,
    checkReadiness: (fetcher = fetch, options = {}) => performReadinessProbe(adapter, fetcher, options)
  };
  return adapter;
}

function stateMessage(adapter: ServiceAdapter, state: ReadinessState, status?: number): string {
  switch (state) {
    case "Ready": return "Validated response received.";
    case "Loading": return "The service reports that its model is loading.";
    case "Unavailable": return `No validated response through the local proxy for ${adapter.name}.`;
    case "Error": return `The service responded with HTTP ${status ?? "error"}.`;
    case "Unknown": return "The response did not match the expected readiness contract.";
  }
}

async function performReadinessProbe(
  adapter: ServiceAdapter,
  fetcher: typeof fetch = fetch,
  options: ProbeOptions = {}
): Promise<ReadinessObservation> {
  const clock = options.clock ?? browserClock;
  const wallNow = options.wallNow ?? (() => new Date());
  const started = clock.now();
  const controller = new AbortController();
  const timer = clock.setTimeout(() => controller.abort(), READINESS_TIMEOUT_MS);
  let state: ReadinessState;
  let message: string;
  let diagnostic = "";

  try {
    const response = await fetcher(`/proxy/${adapter.id}${adapter.probePath}`, {
      headers: { accept: "application/json" },
      signal: controller.signal
    });
    const body = await readBoundedBody(response);
    diagnostic = boundDiagnostic(body.text, body.truncated);

    let payload: unknown;
    if (isJsonMediaType(response.headers.get("content-type")) && !body.truncated) {
      try { payload = JSON.parse(body.text); } catch { payload = undefined; }
    }

    if (response.status === 502 && isRecord(payload) && payload.kind === "proxy-unavailable") {
      state = "Unavailable";
    } else if (adapter.id === "whisper" && response.status === 503 && isRecord(payload) && payload.status === "loading") {
      state = "Loading";
    } else if (!response.ok) {
      state = "Error";
    } else if (!isJsonMediaType(response.headers.get("content-type")) || body.truncated) {
      state = "Unknown";
    } else {
      state = adapter.validateReadiness(payload) ?? "Unknown";
    }
    message = stateMessage(adapter, state, response.status);
  } catch (error) {
    state = "Unavailable";
    message = stateMessage(adapter, state);
    diagnostic = error instanceof Error ? boundDiagnostic(`${error.name}: ${error.message}`) : "Network request failed.";
  } finally {
    clock.clearTimeout(timer);
  }

  return {
    state,
    message,
    diagnostic,
    checkedAt: wallNow().toISOString(),
    latencyMs: Math.max(0, Math.round(clock.now() - started))
  };
}
