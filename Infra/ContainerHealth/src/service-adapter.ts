import { SERVICE_ADAPTERS, type ServiceAdapter as ReadinessAdapter } from "./readiness";
import { LlamaPreparationError, parseLlamaSse, type LlamaModelOption, type LlamaModelState, type LlamaModelPreparer } from "./llama";

export const INPUT_TEXT_LIMIT = 4 * 1_024;
export const WIRE_DIAGNOSTIC_LIMIT = 64 * 1_024;
const TRUNCATED = "\n[truncated]";

export type FormValue = string | boolean | File | null;
export type FormValues = Readonly<Record<string, FormValue>>;
export type FieldControl = "text" | "textarea" | "number" | "checkbox" | "file" | "select";
export type FieldGroup = "common" | "advanced";

export interface FieldOption { readonly value: string; readonly label: string }
export interface FieldDefinition {
  readonly key: string;
  readonly wireKey: string;
  readonly label: string;
  readonly control: FieldControl;
  readonly group: FieldGroup;
  readonly required: boolean;
  readonly initialValue: FormValue;
  readonly help?: string;
  readonly example?: string;
  readonly accept?: string;
  readonly options?: readonly FieldOption[];
  readonly min?: string;
  readonly max?: string;
  readonly step?: string;
}

export interface ValidationResult {
  readonly errors: Readonly<Record<string, string | undefined>>;
  readonly warnings: Readonly<Record<string, string | undefined>>;
}

export interface InputSummaryItem {
  readonly label: string;
  readonly value: string;
}

export type ProgressPhase = "request" | "upload" | "generate" | "convert";
export interface PhaseProgressEvent {
  readonly kind: "phase";
  readonly phase: ProgressPhase;
  readonly status: "started" | "completed";
  readonly message: string;
}
export interface TextDeltaProgressEvent {
  readonly kind: "thinking-delta" | "answer-delta";
  readonly text: string;
}
export type ProgressEvent = PhaseProgressEvent | TextDeltaProgressEvent;
export type ProgressEmitter = (event: ProgressEvent) => void;

export interface LlmResult {
  readonly kind: "llm";
  readonly model: string;
  readonly thinking: string;
  readonly answer: string;
  readonly finishReason?: string;
  readonly usage?: Readonly<Record<string, number>>;
  readonly timing?: Readonly<Record<string, number>>;
}
export interface AudioResult {
  readonly kind: "audio";
  readonly blob: Blob;
  readonly mediaType: "audio/wav";
  readonly filename: string;
  readonly sampleRate?: number;
}
export interface TimingItem {
  readonly text: string;
  readonly start: number;
  readonly end: number;
  readonly probability?: number;
}
export interface TranscriptionResult {
  readonly kind: "transcription";
  readonly format: "json" | "verbose_json" | "text" | "srt" | "vtt";
  readonly text: string;
  readonly language?: string;
  readonly duration?: number;
  readonly segments?: readonly TimingItem[];
  readonly words?: readonly TimingItem[];
}
export interface SimilarityResult { readonly kind: "similarity"; readonly score: number }
export type ServiceResult = LlmResult | AudioResult | TranscriptionResult | SimilarityResult;

export type FailureCategory = "unavailable" | "http" | "protocol";
export interface ServiceFailureShape {
  readonly category: FailureCategory;
  readonly message: string;
  readonly status?: number | undefined;
  readonly serviceMessage?: string | undefined;
  readonly diagnostic: string;
  readonly partialResult?: unknown | undefined;
}

export class ServiceFailure extends Error implements ServiceFailureShape {
  readonly category: FailureCategory;
  readonly status: number | undefined;
  readonly serviceMessage: string | undefined;
  readonly diagnostic: string;
  readonly partialResult: unknown;

  constructor(failure: ServiceFailureShape) {
    super(failure.message);
    this.name = "ServiceFailure";
    this.category = failure.category;
    this.status = failure.status;
    this.serviceMessage = failure.serviceMessage;
    this.diagnostic = failure.diagnostic;
    this.partialResult = failure.partialResult;
  }
}

export interface AdapterExecution {
  readonly result: ServiceResult;
  readonly diagnostic: string;
  readonly warnings: readonly string[];
}

export interface ServiceAdapter extends ReadinessAdapter {
  readonly resultKind: ServiceResult["kind"];
  readonly runLabel: string;
  readonly fields: readonly FieldDefinition[];
  readonly prepareForm?: LlamaModelPreparer;
  initialValues(): FormValues;
  validate(values: FormValues): ValidationResult;
  summarizeInput(values: FormValues): readonly InputSummaryItem[];
  execute(values: FormValues, signal: AbortSignal, progress: ProgressEmitter, fetcher?: typeof fetch): Promise<AdapterExecution>;
}

const similarityFields = Object.freeze([
  Object.freeze({ key: "text1", wireKey: "text1", label: "First text", control: "textarea", group: "common", required: true, initialValue: "", example: "The quick brown fox jumps over the lazy dog." }),
  Object.freeze({ key: "text2", wireKey: "text2", label: "Second text", control: "textarea", group: "common", required: true, initialValue: "", example: "A fast brown fox leaps over a sleepy dog." })
] satisfies readonly FieldDefinition[]);

const llamaFields = Object.freeze([
  Object.freeze({ key: "prompt", wireKey: "messages", label: "Prompt", control: "textarea", group: "common", required: true, initialValue: "", example: "Explain why the sky changes colour at sunset." }),
  Object.freeze({ key: "model", wireKey: "model", label: "Model preset", control: "select", group: "common", required: true, initialValue: "", help: "Prepared from the router; selecting a preset does not load it until Run." }),
  Object.freeze({ key: "temperature", wireKey: "temperature", label: "Temperature", control: "number", group: "advanced", required: true, initialValue: "0.8", step: "0.1" }),
  Object.freeze({ key: "top_p", wireKey: "top_p", label: "Top P", control: "number", group: "advanced", required: true, initialValue: "0.95", step: "0.05" }),
  Object.freeze({ key: "max_tokens", wireKey: "max_tokens", label: "Max tokens", control: "number", group: "advanced", required: true, initialValue: "256", min: "1", step: "1" }),
  Object.freeze({ key: "frequency_penalty", wireKey: "frequency_penalty", label: "Frequency penalty", control: "number", group: "advanced", required: true, initialValue: "0", step: "0.1" }),
  Object.freeze({ key: "presence_penalty", wireKey: "presence_penalty", label: "Presence penalty", control: "number", group: "advanced", required: true, initialValue: "0", step: "0.1" }),
  Object.freeze({ key: "additional_properties", wireKey: "", label: "Additional request properties", control: "textarea", group: "advanced", required: true, initialValue: "{}", help: "A JSON object. model, messages, and stream are reserved." })
] satisfies readonly FieldDefinition[]);

function boundText(value: string, limit: number): string {
  return value.length <= limit ? value : `${value.slice(0, Math.max(0, limit - TRUNCATED.length))}${TRUNCATED}`;
}

function boundedWireDiagnostic(value: string): string {
  return boundText(value, WIRE_DIAGNOSTIC_LIMIT);
}

function summaryValue(value: FormValue): string {
  if (value instanceof File) return `${value.name} · ${value.size} bytes · ${value.type || "unknown MIME type"}`;
  if (value === null) return "Not provided";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  return boundText(value, INPUT_TEXT_LIMIT);
}

function initialValues(fields: readonly FieldDefinition[]): FormValues {
  return Object.freeze(Object.fromEntries(fields.map((field) => [field.key, field.initialValue])));
}

function summarize(fields: readonly FieldDefinition[], values: FormValues): readonly InputSummaryItem[] {
  return Object.freeze(fields.map((field) => Object.freeze({ label: field.label, value: summaryValue(values[field.key] ?? null) })));
}

function isJson(response: Response): boolean {
  const value = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? "";
  return value === "application/json" || value.endsWith("+json");
}

async function boundedResponseText(response: Response): Promise<string> {
  if (response.body === null) return "";
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let text = "";
  let bytes = 0;
  while (true) {
    const next = await reader.read();
    if (next.done) return text + decoder.decode();
    const remaining = WIRE_DIAGNOSTIC_LIMIT - bytes;
    if (next.value.byteLength > remaining) {
      text += decoder.decode(next.value.subarray(0, Math.max(0, remaining)), { stream: true });
      await reader.cancel();
      return `${text}${decoder.decode()}${TRUNCATED}`;
    }
    bytes += next.value.byteLength;
    text += decoder.decode(next.value, { stream: true });
  }
}

function safeServiceMessage(payload: unknown): string | undefined {
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) return undefined;
  const detail = (payload as Record<string, unknown>).detail;
  if (typeof detail === "string" && detail.trim() !== "") return boundText(detail, 1_024);
  if (Array.isArray(detail)) return boundText(JSON.stringify(detail), 1_024);
  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseLlamaModels(payload: unknown): readonly LlamaModelOption[] | undefined {
  if (!isRecord(payload) || payload.object !== "list" || !Array.isArray(payload.data)) return undefined;
  const models: LlamaModelOption[] = [];
  for (const value of payload.data) {
    if (!isRecord(value) || typeof value.id !== "string" || value.id.trim() === "" || !isRecord(value.status)) return undefined;
    if (!Object.hasOwn(value.status, "preset")) continue;
    const rawState = value.status.failed === true ? "failed" : value.status.value;
    if (!(["loaded", "loading", "sleeping", "unloaded", "failed"] as const).includes(rawState as LlamaModelState)) return undefined;
    const state = rawState as LlamaModelState;
    const stateLabel = `${state[0]?.toUpperCase()}${state.slice(1)}`;
    models.push(Object.freeze({ id: value.id, state, label: `${value.id} — ${stateLabel}`, runnable: state !== "failed" }));
  }
  return Object.freeze(models);
}

const prepareLlamaModels: LlamaModelPreparer = async (signal, fetcher = fetch) => {
  let response: Response;
  try {
    response = await fetcher("/proxy/llama/v1/models", { headers: { accept: "application/json" }, signal });
  } catch (error) {
    if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
    throw new LlamaPreparationError(error instanceof Error ? `${error.name}: ${error.message}` : "Model preparation request failed.");
  }
  const diagnostic = await boundedResponseText(response);
  let payload: unknown;
  try { payload = JSON.parse(diagnostic); } catch { payload = undefined; }
  const models = response.ok && isJson(response) && !diagnostic.endsWith(TRUNCATED) ? parseLlamaModels(payload) : undefined;
  if (models === undefined) throw new LlamaPreparationError(diagnostic);
  return Object.freeze({ models, diagnostic });
};

function requireReadinessMetadata(id: "llama" | "minilm-l6" | "mpnet-base-v2") {
  const value = SERVICE_ADAPTERS.find((adapter) => adapter.id === id);
  if (value === undefined) throw new Error(`Missing readiness metadata for ${id}.`);
  return value;
}

function stringValue(values: FormValues, key: string): string | undefined {
  return typeof values[key] === "string" ? values[key] : undefined;
}

function finiteNumberError(value: string | undefined): string | undefined {
  return value === undefined || value.trim() === "" || !Number.isFinite(Number(value)) ? "Enter a finite number." : undefined;
}

function parseAdditionalProperties(value: string | undefined): { value?: Record<string, unknown>; error?: string } {
  let parsed: unknown;
  try { parsed = JSON.parse(value ?? ""); } catch { return { error: "Enter a valid JSON object." }; }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return { error: "Enter a JSON object." };
  const record = parsed as Record<string, unknown>;
  if (["model", "messages", "stream"].some((key) => Object.hasOwn(record, key))) return { error: "Additional properties cannot replace model, messages, or stream." };
  return { value: record };
}

export function createLlamaAdapter(): ServiceAdapter {
  const service = requireReadinessMetadata("llama");
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "llm",
    runLabel: "Run Llama completion",
    fields: llamaFields,
    prepareForm: prepareLlamaModels,
    initialValues: () => initialValues(llamaFields),
    validate(values): ValidationResult {
      const maxTokens = stringValue(values, "max_tokens");
      const additional = parseAdditionalProperties(stringValue(values, "additional_properties"));
      const errors = {
        prompt: stringValue(values, "prompt")?.trim() ? undefined : "Enter a prompt.",
        model: stringValue(values, "model")?.trim() ? undefined : "Choose a prepared model preset.",
        temperature: finiteNumberError(stringValue(values, "temperature")),
        top_p: finiteNumberError(stringValue(values, "top_p")),
        max_tokens: maxTokens !== undefined && /^\d+$/u.test(maxTokens) && Number(maxTokens) > 0 ? undefined : "Enter a positive whole number.",
        frequency_penalty: finiteNumberError(stringValue(values, "frequency_penalty")),
        presence_penalty: finiteNumberError(stringValue(values, "presence_penalty")),
        additional_properties: additional.error
      };
      return { errors: Object.freeze(errors), warnings: Object.freeze({}) };
    },
    summarizeInput: (values) => summarize(llamaFields, values),
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const prompt = stringValue(values, "prompt");
      const model = stringValue(values, "model");
      const additional = parseAdditionalProperties(stringValue(values, "additional_properties"));
      if (prompt === undefined || model === undefined || additional.value === undefined) throw new Error("Llama execution received invalid values.");
      const request = {
        temperature: Number(values.temperature), top_p: Number(values.top_p), max_tokens: Number(values.max_tokens),
        frequency_penalty: Number(values.frequency_penalty), presence_penalty: Number(values.presence_penalty),
        ...additional.value,
        model, messages: [{ role: "user", content: prompt }], stream: true
      };
      const requestDiagnostic = boundedWireDiagnostic(`Request:\n${JSON.stringify(request, null, 2)}`);
      progress({ kind: "phase", phase: "request", status: "started", message: "Requesting streamed completion." });
      let response: Response;
      try {
        response = await fetcher("/proxy/llama/v1/chat/completions", {
          method: "POST", headers: { accept: "text/event-stream", "content-type": "application/json" }, body: JSON.stringify(request), signal
        });
      } catch (error) {
        if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
        throw new ServiceFailure({ category: "unavailable", message: `The dashboard could not reach ${service.name} through the local proxy.`, diagnostic: boundedWireDiagnostic(`${requestDiagnostic}\n${error instanceof Error ? `${error.name}: ${error.message}` : "Network request failed."}`) });
      }
      if (!response.ok) {
        const responseDiagnostic = await boundedResponseText(response);
        let payload: unknown;
        try { payload = JSON.parse(responseDiagnostic); } catch { payload = undefined; }
        if (response.status === 502 && typeof payload === "object" && payload !== null && (payload as Record<string, unknown>).kind === "proxy-unavailable") {
          throw new ServiceFailure({ category: "unavailable", message: `The dashboard could not reach ${service.name} through the local proxy.`, status: 502, diagnostic: boundedWireDiagnostic(`${requestDiagnostic}\nResponse:\n${responseDiagnostic}`) });
        }
        const error = typeof payload === "object" && payload !== null && typeof (payload as { error?: { message?: unknown } }).error?.message === "string"
          ? (payload as { error: { message: string } }).error.message : safeServiceMessage(payload);
        throw new ServiceFailure({ category: "http", message: `The service responded with HTTP ${response.status}.`, status: response.status, ...(error === undefined ? {} : { serviceMessage: error }), diagnostic: boundedWireDiagnostic(`${requestDiagnostic}\nResponse:\n${responseDiagnostic}`) });
      }
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
      if (mediaType !== "text/event-stream" || response.body === null) throw new ServiceFailure({ category: "protocol", message: "The service returned an invalid streaming response.", diagnostic: boundedWireDiagnostic(`${requestDiagnostic}\nContent-Type: ${mediaType ?? "missing"}`) });
      try {
        const parsed = await parseLlamaSse(response.body, signal, progress);
        if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
        progress({ kind: "phase", phase: "request", status: "completed", message: "Streamed completion received." });
        return {
          result: { kind: "llm", model, thinking: parsed.thinking, answer: parsed.answer, ...(parsed.finishReason === undefined ? {} : { finishReason: parsed.finishReason }), ...(parsed.usage === undefined ? {} : { usage: parsed.usage }), ...(parsed.timing === undefined ? {} : { timing: parsed.timing }) },
          diagnostic: boundedWireDiagnostic(`Stream:\n${parsed.diagnostic}\n${requestDiagnostic}`),
          warnings: Object.freeze([])
        };
      } catch (error) {
        if (error instanceof ServiceFailure) throw new ServiceFailure({
          category: error.category, message: error.message, status: error.status, serviceMessage: error.serviceMessage,
          diagnostic: boundedWireDiagnostic(`Stream:\n${error.diagnostic}\n${requestDiagnostic}`), partialResult: error.partialResult
        });
        throw error;
      }
    }
  };
  return Object.freeze(adapter);
}

export function createSimilarityAdapter(id: "minilm-l6" | "mpnet-base-v2"): ServiceAdapter {
  const service = requireReadinessMetadata(id);
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "similarity",
    runLabel: "Run similarity test",
    fields: similarityFields,
    initialValues: () => initialValues(similarityFields),
    validate(values): ValidationResult {
      const errors = {
        text1: typeof values.text1 !== "string" || values.text1.trim() === "" ? "Enter the first text." : undefined,
        text2: typeof values.text2 !== "string" || values.text2.trim() === "" ? "Enter the second text." : undefined
      };
      return { errors: Object.freeze(errors), warnings: Object.freeze({}) };
    },
    summarizeInput: (values) => summarize(similarityFields, values),
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const text1 = values.text1;
      const text2 = values.text2;
      if (typeof text1 !== "string" || typeof text2 !== "string") throw new Error("Similarity execution received invalid values.");
      progress({ kind: "phase", phase: "request", status: "started", message: "Sending similarity request." });
      let response: Response;
      try {
        response = await fetcher(`/proxy/${id}/similarity`, {
          method: "POST",
          headers: { accept: "application/json", "content-type": "application/json" },
          body: JSON.stringify({ text1, text2 }),
          signal
        });
      } catch (error) {
        if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
        throw new ServiceFailure({ category: "unavailable", message: `The dashboard could not reach ${service.name} through the local proxy.`, diagnostic: error instanceof Error ? `${error.name}: ${error.message}` : "Network request failed." });
      }
      const diagnostic = await boundedResponseText(response);
      let payload: unknown;
      if (isJson(response) && !diagnostic.endsWith(TRUNCATED)) {
        try { payload = JSON.parse(diagnostic); } catch { payload = undefined; }
      }
      if (response.status === 502 && typeof payload === "object" && payload !== null && (payload as Record<string, unknown>).kind === "proxy-unavailable") {
        throw new ServiceFailure({ category: "unavailable", message: `The dashboard could not reach ${service.name} through the local proxy.`, status: 502, diagnostic });
      }
      if (!response.ok) {
        const serviceMessage = safeServiceMessage(payload);
        throw new ServiceFailure({ category: "http", message: `The service responded with HTTP ${response.status}.`, status: response.status, diagnostic, ...(serviceMessage === undefined ? {} : { serviceMessage }) });
      }
      if (!isJson(response) || typeof payload !== "object" || payload === null || Array.isArray(payload)) {
        throw new ServiceFailure({ category: "protocol", message: "The service returned an invalid similarity response.", diagnostic });
      }
      const score = (payload as Record<string, unknown>).similarity;
      if (typeof score !== "number" || !Number.isFinite(score)) {
        throw new ServiceFailure({ category: "protocol", message: "The service returned a non-finite or missing similarity score.", diagnostic });
      }
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      progress({ kind: "phase", phase: "request", status: "completed", message: "Similarity response received." });
      const warnings = score < -1 || score > 1 ? ["This raw cosine value is outside the theoretical -1 to 1 range."] : [];
      return { result: { kind: "similarity", score }, diagnostic, warnings: Object.freeze(warnings) };
    }
  };
  return Object.freeze(adapter);
}

export const FUNCTIONAL_ADAPTERS: readonly ServiceAdapter[] = Object.freeze([
  createLlamaAdapter(),
  createSimilarityAdapter("minilm-l6"),
  createSimilarityAdapter("mpnet-base-v2")
]);
