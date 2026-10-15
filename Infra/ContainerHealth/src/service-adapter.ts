import { SERVICE_ADAPTERS, type ServiceAdapter as ReadinessAdapter } from "./readiness";
import { LlamaPreparationError, parseLlamaSse, type LlamaModelOption, type LlamaModelState, type LlamaModelPreparer } from "./llama";
import { audioFilename, describeWav, isAudioMediaType, isDocumentedAudioFile, parseWav } from "./tts";
import { describeUploadWarning, parseTranscription, TRANSCRIPTION_FORMATS, type TranscriptionFormat } from "./whisper";

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
  /** Optional label splitting a large Advanced surface into its own native disclosure group. */
  readonly advancedGroup?: string;
  readonly required: boolean;
  readonly initialValue: FormValue;
  readonly help?: string;
  readonly example?: string;
  readonly accept?: string;
  readonly options?: readonly FieldOption[];
  readonly min?: string;
  readonly max?: string;
  readonly step?: string;
  /** Shown only while the form's model select names one of these; absent means always shown. */
  readonly models?: readonly string[];
}

export interface ValidationResult {
  readonly errors: Readonly<Record<string, string | undefined>>;
  readonly warnings: Readonly<Record<string, string | undefined>>;
}

export interface InputSummaryItem {
  readonly label: string;
  readonly value: string;
}

export type ProgressPhase = "request" | "upload" | "switch" | "generate";
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

/** "busy" is an expected state, not a fault: audio.cpp is generating with another model. */
export type FailureCategory = "unavailable" | "http" | "protocol" | "busy";
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
  /** What the prepared model list is called in the workbench; "model presets" when absent. */
  readonly preparationNoun?: string;
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

/**
 * Maps any non-2xx response to its failure: the proxy's normalized 502 is Unavailable and every
 * other reached status is an HTTP failure carrying the service's own message when it supplies one.
 */
function reachedFailure(options: {
  readonly response: Response;
  readonly payload: unknown;
  readonly serviceName: string;
  readonly diagnostic: string;
  readonly extractMessage?: (payload: unknown) => string | undefined;
}): ServiceFailure {
  const { response, payload, serviceName, diagnostic } = options;
  if (response.status === 502 && isRecord(payload) && payload.kind === "proxy-unavailable") {
    return new ServiceFailure({ category: "unavailable", message: `The dashboard could not reach ${serviceName} through the local proxy.`, status: 502, diagnostic });
  }
  const serviceMessage = (options.extractMessage ?? safeServiceMessage)(payload);
  return new ServiceFailure({
    category: "http", message: `The service responded with HTTP ${response.status}.`, status: response.status,
    ...(serviceMessage === undefined ? {} : { serviceMessage }), diagnostic
  });
}

/** Normalizes a network/connection error, which the proxy never turns into a response. */
function unreachableFailure(serviceName: string, error: unknown, diagnostic: string): ServiceFailure {
  return new ServiceFailure({
    category: "unavailable", message: `The dashboard could not reach ${serviceName} through the local proxy.`,
    diagnostic: boundedWireDiagnostic(`${diagnostic}${diagnostic === "" ? "" : "\n"}${error instanceof Error ? `${error.name}: ${error.message}` : "Network request failed."}`)
  });
}

function parseLlamaModels(payload: unknown): readonly LlamaModelOption[] | undefined {
  if (!isRecord(payload) || payload.object !== "list" || !Array.isArray(payload.data)) return undefined;
  const models: LlamaModelOption[] = [];
  for (const value of payload.data) {
    if (!isRecord(value) || typeof value.id !== "string" || value.id.trim() === "" || !isRecord(value.status)) return undefined;
    if (!Object.hasOwn(value.status, "preset")) continue;
    const rawState = value.status.failed === true ? "failed" : value.status.value;
    if (!(["loaded", "loading", "sleeping", "unloaded", "failed"] as readonly LlamaModelState[]).includes(rawState as LlamaModelState)) return undefined;
    const state = rawState as LlamaModelState;
    const stateLabel = `${state[0]?.toUpperCase()}${state.slice(1)}`;
    models.push(Object.freeze({ id: value.id, state, label: `${value.id} — ${stateLabel}`, runnable: state !== "failed" }));
  }
  return Object.freeze(models);
}

function modelPreparer(route: string, parse: (payload: unknown) => readonly LlamaModelOption[] | undefined): LlamaModelPreparer {
  return async (signal, fetcher = fetch) => {
    let response: Response;
    try {
      response = await fetcher(route, { headers: { accept: "application/json" }, signal });
    } catch (error) {
      if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
      throw new LlamaPreparationError(error instanceof Error ? `${error.name}: ${error.message}` : "Model preparation request failed.");
    }
    const diagnostic = await boundedResponseText(response);
    let payload: unknown;
    try { payload = JSON.parse(diagnostic); } catch { payload = undefined; }
    const models = response.ok && isJson(response) && !diagnostic.endsWith(TRUNCATED) ? parse(payload) : undefined;
    if (models === undefined) throw new LlamaPreparationError(diagnostic);
    return Object.freeze({ models, diagnostic });
  };
}

const prepareLlamaModels = modelPreparer("/proxy/llama/v1/models", parseLlamaModels);

/** Llama and audio.cpp both report failures through the OpenAI-style error envelope. */
function openAiErrorMessage(value: unknown): string | undefined {
  return typeof (value as { error?: { message?: unknown } })?.error?.message === "string"
    ? boundText((value as { error: { message: string } }).error.message, 1_024) : safeServiceMessage(value);
}

function requireReadinessMetadata(id: ReadinessAdapter["id"]) {
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
        throw unreachableFailure(service.name, error, requestDiagnostic);
      }
      if (!response.ok) {
        const responseDiagnostic = await boundedResponseText(response);
        let payload: unknown;
        try { payload = JSON.parse(responseDiagnostic); } catch { payload = undefined; }
        throw reachedFailure({
          response, payload, serviceName: service.name,
          diagnostic: boundedWireDiagnostic(`${requestDiagnostic}\nResponse:\n${responseDiagnostic}`),
          extractMessage: openAiErrorMessage
        });
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
        throw unreachableFailure(service.name, error, "");
      }
      const diagnostic = await boundedResponseText(response);
      let payload: unknown;
      if (isJson(response) && !diagnostic.endsWith(TRUNCATED)) {
        try { payload = JSON.parse(diagnostic); } catch { payload = undefined; }
      }
      if (!response.ok) throw reachedFailure({ response, payload, serviceName: service.name, diagnostic });
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

function isIntegerField(field: FieldDefinition): boolean {
  return field.control === "number" && field.step === "1";
}

/**
 * Validates one non-file field. Numeric controls block only known-invalid shapes because these
 * services enforce no ranges of their own, and a cleared optional value is omitted rather than sent.
 */
function fieldValueError(field: FieldDefinition, value: FormValue): string | undefined {
  const text = typeof value === "string" ? value : "";
  if (field.control === "textarea" || field.control === "text") {
    return field.required && text.trim() === "" ? `Enter ${field.label.toLowerCase()}.` : undefined;
  }
  if (field.control === "select") {
    return (field.options ?? []).some((option) => option.value === text) ? undefined : "Choose a supported option.";
  }
  if (field.control !== "number") return undefined;
  if (text.trim() === "") return field.required ? "Enter a finite number." : undefined;
  return isIntegerField(field)
    ? (/^-?\d+$/u.test(text.trim()) ? undefined : "Enter a whole number.")
    : finiteNumberError(text);
}

const OMITTED_HELP = "Optional. Left blank it is omitted from the request and the service default applies.";
const AUDIO_ACCEPT = ".wav,.mp3,audio/wav,audio/x-wav,audio/mpeg";
const SPEECH_EXAMPLE = "It was a bright cold day in April, and the clocks were striking thirteen.";

const AUDIOCPP_SPEECH_ROUTE = "/proxy/audiocpp/v1/audio/speech";
const AUDIOCPP_MODELS_ROUTE = "/proxy/audiocpp/v1/models";
/** audio.cpp's cap on an inline reference, which is also the app's Reference Limit byte bound. */
export const AUDIOCPP_REFERENCE_LIMIT_BYTES = 5 * 1_024 * 1_024;
const SEED_MAX = 2_147_483_647;

export type AudioCppModelId = "breeze-q8" | "breeze-design" | "voxcpm2" | "chatterbox" | "qwen3-base" | "qwen3-design";
export interface AudioCppModel { readonly id: AudioCppModelId; readonly family: string; readonly task: string; readonly role: string }

/** The default entries of Infra/audiocpp/server.json. Each id fixes the request shape the dashboard sends. */
export const AUDIOCPP_MODELS: readonly AudioCppModel[] = Object.freeze([
  Object.freeze({ id: "breeze-q8", family: "breeze_tts", task: "clon", role: "Breeze paragraph TTS" }),
  Object.freeze({ id: "breeze-design", family: "breeze_tts", task: "tts", role: "Breeze voice design" }),
  Object.freeze({ id: "voxcpm2", family: "voxcpm2", task: "tts", role: "VoxCPM2 paragraph TTS and voice design" }),
  Object.freeze({ id: "chatterbox", family: "chatterbox", task: "clon", role: "Chatterbox paragraph TTS" }),
  Object.freeze({ id: "qwen3-base", family: "qwen3_tts", task: "tts", role: "Qwen3 Base paragraph TTS" }),
  Object.freeze({ id: "qwen3-design", family: "qwen3_tts", task: "vdes", role: "Qwen3 voice design" })
] satisfies readonly AudioCppModel[]);

function audioCppModel(id: string | undefined): AudioCppModel | undefined {
  return AUDIOCPP_MODELS.find((model) => model.id === id);
}

/**
 * Cross-checks the live list against the table: a table id audio.cpp does not list, or lists under another
 * family or task, cannot be run with the shape the table fixes, and a live id outside the table has no shape.
 */
export function parseAudioCppModels(payload: unknown): readonly LlamaModelOption[] | undefined {
  if (!isRecord(payload) || payload.object !== "list" || !Array.isArray(payload.data)) return undefined;
  const live = new Map<string, Record<string, unknown>>();
  for (const value of payload.data) {
    if (!isRecord(value) || typeof value.id !== "string" || value.id.trim() === "" || typeof value.loaded !== "boolean") return undefined;
    live.set(value.id, value);
  }
  const option = (id: string, state: LlamaModelState, label: string, runnable: boolean): LlamaModelOption => Object.freeze({ id, state, label, runnable });
  const models = AUDIOCPP_MODELS.map((model) => {
    const item = live.get(model.id);
    if (item === undefined) return option(model.id, "unconfigured", `${model.id} — Not configured`, false);
    if ((typeof item.family === "string" && item.family !== model.family) || (typeof item.task === "string" && item.task !== model.task)) {
      return option(model.id, "unknown", `${model.id} — Unexpected ${String(item.family)}/${String(item.task)}`, false);
    }
    return item.loaded === true
      ? option(model.id, "loaded", `${model.id} — Loaded · ${model.role}`, true)
      : option(model.id, "unloaded", `${model.id} — Not loaded · ${model.role}`, true);
  });
  for (const id of live.keys()) {
    if (audioCppModel(id) === undefined) models.push(option(id, "unknown", `${id} — Unknown to the dashboard`, false));
  }
  return Object.freeze(models);
}

const AUDIOCPP_LANGUAGES: readonly string[] = Object.freeze([
  "auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"
]);

const audioCppFields = Object.freeze([
  Object.freeze({
    key: "model", wireKey: "model", label: "Model", control: "select", group: "common", required: true, initialValue: "",
    help: "Prepared from audio.cpp's model list. A model that is not loaded is switched in on Run, which takes 9–31 s."
  }),
  Object.freeze({ key: "text", wireKey: "input", label: "Text to speak", control: "textarea", group: "common", required: true, initialValue: "", example: SPEECH_EXAMPLE }),
  Object.freeze({
    key: "design_prompt", wireKey: "options.instruction", label: "Design prompt", control: "textarea", group: "common", required: true, initialValue: "",
    models: Object.freeze(["breeze-design", "qwen3-design"]),
    help: "Describes the voice to design. Sent as options.instruction, with no reference audio.",
    example: "A calm middle-aged British narrator with a warm, measured delivery."
  }),
  Object.freeze({
    key: "instruction", wireKey: "options.instruction", label: "Instruction", control: "text", group: "common", required: false, initialValue: "",
    models: Object.freeze(["breeze-q8", "voxcpm2"]),
    help: "Optional. Breeze sends it as options.instruction with guidance_scale 3 (1 without); VoxCPM2 prepends it to the text as (instruction)text.",
    example: "whispering"
  }),
  Object.freeze({
    key: "reference_audio", wireKey: "voice_ref", label: "Reference audio", control: "file", group: "common", required: false, initialValue: null,
    models: Object.freeze(["breeze-q8", "chatterbox", "qwen3-base", "voxcpm2"]), accept: AUDIO_ACCEPT,
    help: "Voice-cloning clip, sent inline as base64, up to 5 MiB. Required except for voxcpm2, where it is optional."
  }),
  Object.freeze({
    key: "reference_text", wireKey: "reference_text", label: "Reference transcript", control: "textarea", group: "common", required: true, initialValue: "",
    models: Object.freeze(["breeze-q8", "qwen3-base"]),
    help: "The exact words spoken in the reference clip.", example: "The quick brown fox jumps over the lazy dog."
  }),
  Object.freeze({
    key: "language", wireKey: "language", label: "Language", control: "select", group: "common", required: true, initialValue: "auto",
    models: Object.freeze(["qwen3-base", "qwen3-design"]),
    help: "Sent top-level as a language name; audio.cpp ignores options.language.",
    options: Object.freeze(AUDIOCPP_LANGUAGES.map((value) => Object.freeze({ value, label: value })))
  }),
  Object.freeze({
    key: "seed", wireKey: "options.seed", label: "Seed", control: "number", group: "advanced", required: false, initialValue: "", min: "0", step: "1",
    help: "Blank draws a fresh random seed for every run, as the app does; audio.cpp's own default is a fixed 0."
  })
] satisfies readonly FieldDefinition[]);

function shownFor(field: FieldDefinition, model: string | undefined): boolean {
  return field.models === undefined || (model !== undefined && field.models.includes(model));
}

function trimmed(values: FormValues, key: string): string {
  return stringValue(values, key)?.trim() ?? "";
}

/** The values the app sends that this console fixes rather than exposes; they mirror the app's setting defaults. */
export function audioCppFixedValues(model: AudioCppModelId, instructed: boolean): { readonly language?: string; readonly options: Readonly<Record<string, string>> } {
  switch (model) {
    case "breeze-q8": return { options: { guidance_scale: instructed ? "3" : "1", temperature: "0.9", top_k: "50", top_p: "1" } };
    case "breeze-design": return { options: { guidance_scale: "3" } };
    case "voxcpm2": return {
      options: {
        guidance_scale: "2", num_inference_steps: "10", min_tokens: "2", max_tokens: "4096",
        retry_badcase: "true", retry_badcase_max_times: "3", retry_badcase_ratio_threshold: "6"
      }
    };
    // audio.cpp's documented Chatterbox defaults differ from its code, so every knob is explicit.
    case "chatterbox": return {
      language: "en",
      options: { exaggeration: "0.5", guidance_scale: "0.5", temperature: "0.8", min_p: "0.05", top_p: "1", repetition_penalty: "1.2" }
    };
    case "qwen3-base":
    case "qwen3-design": return { options: {} };
  }
}

export interface AudioCppSpeechRequest {
  readonly model: AudioCppModelId;
  readonly input: string;
  readonly voice_ref?: { readonly type: "base64"; readonly data: string };
  readonly reference_text?: string;
  readonly language?: string;
  readonly options: Readonly<Record<string, string>>;
}

/** Builds the per-family request shape documented in Infra/README.md, "audio.cpp (TTS)". */
export function buildAudioCppRequest(model: AudioCppModelId, values: FormValues, voiceRef: string | undefined, seed: string): AudioCppSpeechRequest {
  const text = stringValue(values, "text") ?? "";
  const instruction = model === "breeze-design" || model === "qwen3-design" ? trimmed(values, "design_prompt")
    : model === "breeze-q8" || model === "voxcpm2" ? trimmed(values, "instruction") : "";
  const fixed = audioCppFixedValues(model, instruction !== "");
  const options: Record<string, string> = { ...fixed.options };
  if (instruction !== "" && model !== "voxcpm2") options.instruction = instruction;
  options.seed = seed;
  const language = model === "qwen3-base" || model === "qwen3-design" ? stringValue(values, "language") : fixed.language;
  const referenceText = model === "breeze-q8" || model === "qwen3-base" ? stringValue(values, "reference_text") : undefined;
  return {
    model,
    input: model === "voxcpm2" && instruction !== "" ? `(${instruction})${text}` : text,
    ...(voiceRef === undefined ? {} : { voice_ref: { type: "base64" as const, data: voiceRef } }),
    ...(referenceText === undefined ? {} : { reference_text: referenceText }),
    ...(language === undefined ? {} : { language }),
    options
  };
}

function validateAudioCpp(values: FormValues): ValidationResult {
  const errors: Record<string, string | undefined> = {};
  const warnings: Record<string, string | undefined> = {};
  const model = audioCppModel(stringValue(values, "model"))?.id;
  errors.model = model === undefined ? "Choose a prepared model." : undefined;
  for (const field of audioCppFields) {
    if (field.key === "model" || !shownFor(field, model)) continue;
    const value = values[field.key] ?? null;
    if (field.control === "file") {
      if (!(value instanceof File) || value.size === 0) {
        if (model !== "voxcpm2") errors[field.key] = "Choose a reference audio file.";
      } else if (value.size > AUDIOCPP_REFERENCE_LIMIT_BYTES) {
        errors[field.key] = "Choose a reference audio file of 5 MiB or less.";
      } else if (!isDocumentedAudioFile(value)) {
        warnings[field.key] = "WAV and MP3 are the documented inputs; this file is still sent because the service decoder may accept it.";
      }
      continue;
    }
    errors[field.key] = fieldValueError(field, value);
  }
  const seed = trimmed(values, "seed");
  if (errors.seed === undefined && seed !== "" && (Number(seed) < 0 || Number(seed) > SEED_MAX)) errors.seed = `Enter a seed from 0 to ${SEED_MAX}.`;
  return { errors: Object.freeze(errors), warnings: Object.freeze(warnings) };
}

function summarizeAudioCpp(values: FormValues): readonly InputSummaryItem[] {
  const model = audioCppModel(stringValue(values, "model"))?.id;
  const items: InputSummaryItem[] = audioCppFields.filter((field) => shownFor(field, model)).map((field) => Object.freeze({
    label: field.label,
    value: field.key === "seed" && trimmed(values, "seed") === "" ? "Random for this run" : summaryValue(values[field.key] ?? null)
  }));
  if (model !== undefined) {
    const instructed = model === "breeze-q8" && trimmed(values, "instruction") !== "";
    const fixed = audioCppFixedValues(model, instructed);
    const shown = [...(fixed.language === undefined ? [] : [`language: ${fixed.language}`]), ...Object.entries(fixed.options).map(([key, value]) => `${key}: ${value}`)];
    items.push(Object.freeze({ label: "Fixed request values", value: shown.length === 0 ? "None" : shown.join(" · ") }));
  }
  return Object.freeze(items);
}

async function base64Of(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8_000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8_000));
  return btoa(binary);
}

/** Whether Run will make audio.cpp switch models; undefined when the list cannot say, which never blocks the run. */
async function audioCppSwitches(model: AudioCppModelId, signal: AbortSignal, fetcher: typeof fetch): Promise<{ readonly switches: boolean | undefined; readonly diagnostic: string }> {
  try {
    const response = await fetcher(AUDIOCPP_MODELS_ROUTE, { headers: { accept: "application/json" }, signal });
    const body = await boundedResponseText(response);
    let payload: unknown;
    try { payload = JSON.parse(body); } catch { payload = undefined; }
    const item = response.ok && isRecord(payload) && Array.isArray(payload.data)
      ? payload.data.find((value) => isRecord(value) && value.id === model) : undefined;
    const switches = isRecord(item) && typeof item.loaded === "boolean" ? !item.loaded : undefined;
    return { switches, diagnostic: `Loaded check: ${switches === undefined ? "unknown" : switches ? `${model} not loaded` : `${model} loaded`}` };
  } catch (error) {
    if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
    return { switches: undefined, diagnostic: "Loaded check: unknown" };
  }
}

export function createAudioCppAdapter(random: () => number = Math.random): ServiceAdapter {
  const service = requireReadinessMetadata("audiocpp");
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "audio",
    runLabel: "Generate speech",
    fields: audioCppFields,
    prepareForm: modelPreparer(AUDIOCPP_MODELS_ROUTE, parseAudioCppModels),
    preparationNoun: "models",
    initialValues: () => initialValues(audioCppFields),
    validate: validateAudioCpp,
    summarizeInput: summarizeAudioCpp,
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const model = audioCppModel(stringValue(values, "model"))?.id;
      if (model === undefined) throw new Error("audio.cpp execution received an unknown model.");
      const field = audioCppFields.find(({ key }) => key === "reference_audio")!;
      const file = shownFor(field, model) && values.reference_audio instanceof File && values.reference_audio.size > 0 ? values.reference_audio : undefined;
      let voiceRef: string | undefined;
      if (file !== undefined) {
        progress({ kind: "phase", phase: "upload", status: "started", message: "Encoding the reference audio." });
        voiceRef = await base64Of(file);
        if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
        progress({ kind: "phase", phase: "upload", status: "completed", message: "Reference audio encoded." });
      }
      const seedText = trimmed(values, "seed");
      const seed = seedText === "" ? String(Math.floor(random() * SEED_MAX)) : String(Number(seedText));
      const request = buildAudioCppRequest(model, values, voiceRef, seed);
      // The reference clip never enters diagnostics: only its name and size.
      const shown = file === undefined ? request : { ...request, voice_ref: { type: "base64", data: `[${file.name} · ${file.size} bytes]` } };
      const loadedCheck = await audioCppSwitches(model, signal, fetcher);
      const requestDiagnostic = boundedWireDiagnostic(`${loadedCheck.diagnostic}\nRequest:\nPOST ${AUDIOCPP_SPEECH_ROUTE}\n${JSON.stringify(shown, null, 2)}`);
      const phase = loadedCheck.switches === true ? "switch" : "request";
      progress({
        kind: "phase", phase, status: "started",
        message: phase === "switch" ? `Switching audio.cpp to ${model} and generating; the first request after a switch takes 9–31 s.` : "Sending the speech request."
      });
      let response: Response;
      try {
        response = await fetcher(AUDIOCPP_SPEECH_ROUTE, {
          method: "POST", headers: { accept: "audio/wav", "content-type": "application/json" }, body: JSON.stringify(request), signal
        });
      } catch (error) {
        if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
        throw unreachableFailure(service.name, error, requestDiagnostic);
      }
      progress({ kind: "phase", phase, status: "completed", message: "The service answered the request." });
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? "missing";
      const responseHeading = `${requestDiagnostic}\nResponse:\nHTTP ${response.status}\nContent-Type: ${mediaType}`;
      if (!response.ok) {
        const body = await boundedResponseText(response);
        let payload: unknown;
        if (isJson(response) && !body.endsWith(TRUNCATED)) {
          try { payload = JSON.parse(body); } catch { payload = undefined; }
        }
        const diagnostic = boundedWireDiagnostic(`${responseHeading}\n${body}`);
        if (response.status === 503 && isRecord(payload) && isRecord(payload.error) && payload.error.type === "server_busy") {
          const serviceMessage = openAiErrorMessage(payload);
          throw new ServiceFailure({
            category: "busy", message: "audio.cpp is generating with another model; run again when it finishes.", status: 503,
            ...(serviceMessage === undefined ? {} : { serviceMessage }), diagnostic
          });
        }
        throw reachedFailure({ response, payload, serviceName: service.name, diagnostic, extractMessage: openAiErrorMessage });
      }
      if (!isAudioMediaType(response.headers.get("content-type"))) {
        const body = await boundedResponseText(response);
        throw new ServiceFailure({ category: "protocol", message: "The service returned a success response that is not audio.", diagnostic: boundedWireDiagnostic(`${responseHeading}\n${body}`) });
      }
      progress({ kind: "phase", phase: "generate", status: "started", message: "Receiving generated audio." });
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      const parsed = parseWav(bytes);
      // Audio bytes never enter diagnostics: only headers, byte count, and parsed format.
      if (!parsed.ok) {
        throw new ServiceFailure({ category: "protocol", message: `The service returned invalid WAV audio. ${parsed.reason}`, diagnostic: boundedWireDiagnostic(`${responseHeading}\nBytes: ${bytes.byteLength}\n${parsed.reason}`) });
      }
      progress({ kind: "phase", phase: "generate", status: "completed", message: "Generated audio received." });
      return {
        result: {
          kind: "audio", blob: new Blob([bytes], { type: "audio/wav" }), mediaType: "audio/wav",
          filename: audioFilename("audiocpp"), sampleRate: parsed.format.sampleRate
        },
        diagnostic: boundedWireDiagnostic(`${responseHeading}\nBytes: ${bytes.byteLength}\n${describeWav(parsed.format)}`),
        warnings: Object.freeze([])
      };
    }
  };
  return Object.freeze(adapter);
}

const WHISPER_ROUTE = "/proxy/whisper/inference";
const WHISPER_ENGLISH_ONLY = "The mounted base.en model is English-only, so this is still sent but the transcript may be wrong.";

function whisperNumber(advancedGroup: string, key: string, label: string, initialValue: string, step: string, help: string): FieldDefinition {
  return Object.freeze({ key, wireKey: key, label, control: "number", group: "advanced", advancedGroup, required: false, initialValue, step, help });
}

function whisperCheckbox(advancedGroup: string, key: string, label: string, initialValue: boolean, help: string): FieldDefinition {
  return Object.freeze({ key, wireKey: key, label, control: "checkbox", group: "advanced", advancedGroup, required: false, initialValue, help });
}

const SENT_EXPLICITLY = "Sent explicitly on every request.";

const whisperFields = Object.freeze([
  Object.freeze({
    key: "file", wireKey: "file", label: "Audio file", control: "file", group: "common", required: true, initialValue: null,
    // WHISPER_FFMPEG=OFF in the image: the service decodes WAV itself and converts nothing.
    accept: ".wav,audio/wav", help: "Required. WAV is the only supported input. A file that is not Canonical WAV is still sent, with a warning."
  }),
  Object.freeze({
    key: "response_format", wireKey: "response_format", label: "Response format", control: "select", group: "common", required: true,
    initialValue: "verbose_json", help: "Verbose JSON is the confirmation default and the only format carrying word timings.",
    options: Object.freeze([
      Object.freeze({ value: "json", label: "json — transcript only" }),
      Object.freeze({ value: "verbose_json", label: "verbose_json — transcript, segments, and words" }),
      Object.freeze({ value: "text", label: "text — plain transcript" }),
      Object.freeze({ value: "srt", label: "srt — subtitles" }),
      Object.freeze({ value: "vtt", label: "vtt — subtitles" })
    ])
  }),
  Object.freeze({
    key: "language", wireKey: "language", label: "Language", control: "text", group: "common", required: true, initialValue: "en",
    help: "The mounted model is base.en, so en is the only accurate choice.", example: "en"
  }),
  Object.freeze({
    key: "token_timestamps", wireKey: "token_timestamps", label: "Word timestamps", control: "checkbox", group: "common",
    required: false, initialValue: true, help: `${SENT_EXPLICITLY} Required for word-level alignment in verbose JSON.`
  }),

  whisperNumber("Slicing and context", "offset_t", "Time offset (ms)", "0", "1", "Start offset in milliseconds."),
  whisperNumber("Slicing and context", "offset_n", "Chunk offset", "0", "1", "Start offset in chunks."),
  whisperNumber("Slicing and context", "duration", "Duration (ms)", "0", "1", "Audio duration to process; 0 processes everything."),
  whisperNumber("Slicing and context", "max_context", "Maximum context", "-1", "1", "Maximum text context kept between segments; -1 keeps the service default."),
  whisperNumber("Slicing and context", "max_len", "Maximum segment length", "1", "1", "Maximum segment length in characters; 1 with Split on word yields one word per segment."),
  whisperNumber("Slicing and context", "audio_ctx", "Audio context size", "0", "1", "Audio context size; 0 keeps the service default."),

  whisperNumber("Decoding", "best_of", "Best of", "2", "1", "Candidates sampled per decode."),
  whisperNumber("Decoding", "beam_size", "Beam size", "-1", "1", "Beam search width; -1 disables beam search."),
  whisperNumber("Decoding", "temperature", "Temperature", "0", "0.1", "Sampling randomness."),
  whisperNumber("Decoding", "temperature_inc", "Temperature increment", "0.2", "0.1", "Temperature step used on decoder fallback."),
  whisperNumber("Decoding", "entropy_thold", "Entropy threshold", "2.4", "0.1", "Entropy threshold for decoder fallback."),
  whisperNumber("Decoding", "logprob_thold", "Log probability threshold", "-1", "0.1", "Log probability threshold for decoder fallback."),
  whisperNumber("Decoding", "no_speech_thold", "No-speech threshold", "0.6", "0.1", "Probability above which a segment is treated as silence."),
  whisperNumber("Decoding", "word_thold", "Word threshold", "0.01", "0.01", "Word timestamp probability threshold."),

  whisperCheckbox("Language and task", "translate", "Translate to English", false, `${SENT_EXPLICITLY} ${WHISPER_ENGLISH_ONLY}`),
  whisperCheckbox("Language and task", "detect_language", "Detect language", false, `${SENT_EXPLICITLY} ${WHISPER_ENGLISH_ONLY}`),
  Object.freeze({
    key: "prompt", wireKey: "prompt", label: "Initial prompt", control: "text", group: "advanced", advancedGroup: "Language and task",
    required: false, initialValue: "", help: OMITTED_HELP, example: "Read2Me, Winston, Oceania"
  }),
  whisperCheckbox("Language and task", "carry_initial_prompt", "Carry initial prompt", false, `${SENT_EXPLICITLY} Re-sends the initial prompt with every segment.`),

  whisperCheckbox("Timing and output", "no_timestamps", "No timestamps", false, `${SENT_EXPLICITLY} Suppresses all timing output.`),
  whisperCheckbox("Timing and output", "split_on_word", "Split on word", true, `${SENT_EXPLICITLY} Splits segments on word rather than token boundaries.`),
  whisperCheckbox("Timing and output", "no_language_probabilities", "No language probabilities", false, `${SENT_EXPLICITLY} Omits per-language probabilities.`),

  whisperCheckbox("Speech and speakers", "diarize", "Diarize", false, `${SENT_EXPLICITLY} Stereo speaker diarization.`),
  whisperCheckbox("Speech and speakers", "tinydiarize", "Tinydiarize", false, `${SENT_EXPLICITLY} Requires a tdrz-enabled model.`),
  whisperCheckbox("Speech and speakers", "suppress_non_speech", "Suppress non-speech segments", false, `${SENT_EXPLICITLY} Suppresses non-speech segments.`),
  whisperCheckbox("Speech and speakers", "suppress_nst", "Suppress non-speech tokens", false, `${SENT_EXPLICITLY} Suppresses non-speech tokens.`),
  whisperCheckbox("Speech and speakers", "debug_mode", "Debug mode", false, `${SENT_EXPLICITLY} Enables verbose service-side logging.`),

  whisperCheckbox("Voice activity detection", "vad", "Enable VAD", false, `${SENT_EXPLICITLY} Compose supplies no VAD model.`),
  whisperNumber("Voice activity detection", "vad_threshold", "VAD threshold", "0.5", "0.1", "Speech probability threshold."),
  whisperNumber("Voice activity detection", "vad_min_speech_duration_ms", "VAD minimum speech (ms)", "250", "1", "Shortest accepted speech run."),
  whisperNumber("Voice activity detection", "vad_min_silence_duration_ms", "VAD minimum silence (ms)", "100", "1", "Shortest silence that splits speech."),
  // FLT_MAX sits on no 0.1 lattice and the service constrains no step, so any step risks a browser
  // reporting this service default as natively invalid. Chromium does not, but Firefox is only checked by hand.
  whisperNumber("Voice activity detection", "vad_max_speech_duration_s", "VAD maximum speech (s)", "3.402823466e38", "any", "Longest accepted speech run."),
  whisperNumber("Voice activity detection", "vad_speech_pad_ms", "VAD speech pad (ms)", "30", "1", "Padding added around detected speech."),
  whisperNumber("Voice activity detection", "vad_samples_overlap", "VAD samples overlap", "0.1", "0.1", "Overlap between analysed windows.")
] satisfies readonly FieldDefinition[]);

function booleanValue(values: FormValues, key: string): boolean {
  return values[key] === true;
}

/**
 * Blocks only what the service itself cannot accept. Every combination the request parser accepts but
 * this Compose deployment cannot honour is a warning, so an independently edited value is never rewritten.
 */
function validateWhisper(values: FormValues): ValidationResult {
  const errors: Record<string, string | undefined> = {};
  const warnings: Record<string, string | undefined> = {};
  for (const field of whisperFields) {
    if (field.control === "checkbox") continue;
    const value = values[field.key] ?? null;
    if (field.control === "file") {
      if (!(value instanceof File) || value.size === 0) errors[field.key] = "Choose a WAV audio file.";
      else if (!/\.wav$/iu.test(value.name) && value.type.toLowerCase() !== "audio/wav") {
        warnings[field.key] = "WAV is the only supported input and this service converts nothing, so this file is likely to be rejected.";
      }
      continue;
    }
    errors[field.key] = fieldValueError(field, value);
  }

  const maxLen = stringValue(values, "max_len") ?? "";
  const requestsTimings = booleanValue(values, "token_timestamps") || booleanValue(values, "split_on_word")
    || (maxLen.trim() !== "" && Number(maxLen) !== 0);
  if (booleanValue(values, "no_timestamps") && requestsTimings) {
    warnings.no_timestamps = "No timestamps produces no timings at all, which conflicts with the word-timing controls that are still enabled.";
  }
  if (booleanValue(values, "split_on_word") && !booleanValue(values, "token_timestamps")) {
    warnings.split_on_word = "Word timestamps are off, so splitting on word has no word-level timing to split.";
  }
  const language = stringValue(values, "language") ?? "";
  if (errors.language === undefined && language.trim() !== "" && language.trim().toLowerCase() !== "en") {
    warnings.language = WHISPER_ENGLISH_ONLY;
  }
  if (booleanValue(values, "detect_language")) warnings.detect_language = WHISPER_ENGLISH_ONLY;
  if (booleanValue(values, "translate")) warnings.translate = WHISPER_ENGLISH_ONLY;
  if (booleanValue(values, "vad")) {
    warnings.vad = "This Compose deployment supplies no VAD model, so enabling VAD is expected to fail.";
  }
  return { errors: Object.freeze(errors), warnings: Object.freeze(warnings) };
}

function buildWhisperRequest(values: FormValues): { readonly form: FormData; readonly summary: string } {
  const form = new FormData();
  const lines: string[] = [];
  for (const field of whisperFields) {
    const value = values[field.key] ?? null;
    if (value instanceof File) {
      form.append(field.wireKey, value, value.name);
      lines.push(`${field.wireKey}: ${value.name} · ${value.size} bytes · ${value.type || "unknown MIME type"}`);
      continue;
    }
    // Checkboxes are sent explicitly so the request never depends on a service-side default.
    if (field.control === "checkbox") {
      form.append(field.wireKey, String(value === true));
      lines.push(`${field.wireKey}: ${String(value === true)}`);
      continue;
    }
    if (typeof value !== "string") continue;
    if (!field.required && value.trim() === "") continue;
    form.append(field.wireKey, value);
    lines.push(`${field.wireKey}: ${boundText(value, INPUT_TEXT_LIMIT)}`);
  }
  return { form, summary: lines.join("\n") };
}

export function createWhisperAdapter(): ServiceAdapter {
  const service = requireReadinessMetadata("whisper");
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "transcription",
    runLabel: "Transcribe audio",
    fields: whisperFields,
    initialValues: () => initialValues(whisperFields),
    validate: validateWhisper,
    summarizeInput: (values) => summarize(whisperFields, values),
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const file = values.file;
      if (!(file instanceof File)) throw new Error("Whisper execution received invalid values.");
      const format = stringValue(values, "response_format");
      if (format === undefined || !TRANSCRIPTION_FORMATS.includes(format as TranscriptionFormat)) {
        throw new Error("Whisper execution received an unsupported response format.");
      }
      progress({ kind: "phase", phase: "upload", status: "started", message: "Reading and checking the audio file." });
      const uploadWarning = await describeUploadWarning(file);
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      progress({ kind: "phase", phase: "upload", status: "completed", message: "The audio file is ready to send." });

      const { form, summary } = buildWhisperRequest(values);
      const requestDiagnostic = boundedWireDiagnostic(`Request:\nPOST ${WHISPER_ROUTE}\nContent-Type: multipart/form-data\n${summary}`);
      progress({ kind: "phase", phase: "request", status: "started", message: "Sending the transcription request." });
      let response: Response;
      try {
        response = await fetcher(WHISPER_ROUTE, { method: "POST", headers: { accept: "application/json, text/plain" }, body: form, signal });
      } catch (error) {
        if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
        throw unreachableFailure(service.name, error, requestDiagnostic);
      }
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? "missing";
      const responseHeading = `${requestDiagnostic}\nResponse:\nHTTP ${response.status}\nContent-Type: ${mediaType}`;
      if (!response.ok) {
        const body = await boundedResponseText(response);
        let payload: unknown;
        if (isJson(response) && !body.endsWith(TRUNCATED)) {
          try { payload = JSON.parse(body); } catch { payload = undefined; }
        }
        throw reachedFailure({
          response, payload, serviceName: service.name, diagnostic: boundedWireDiagnostic(`${responseHeading}\n${body}`),
          // Whisper reports request errors as a JSON object with an error string.
          extractMessage: (value) => isRecord(value) && typeof value.error === "string" && value.error.trim() !== ""
            ? boundText(value.error, 1_024) : safeServiceMessage(value)
        });
      }
      // The transcript itself is never truncated; only the diagnostic copy is bounded.
      const body = await response.text();
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      const diagnostic = boundedWireDiagnostic(`${responseHeading}\n${body}`);
      const parsed = parseTranscription({
        format: format as TranscriptionFormat, body, isJsonMediaType: isJson(response),
        wordTimestampsRequested: booleanValue(values, "token_timestamps"), diagnostic
      });
      progress({ kind: "phase", phase: "request", status: "completed", message: "Transcript received." });
      return {
        result: parsed.result,
        diagnostic,
        warnings: Object.freeze([...(uploadWarning === undefined ? [] : [uploadWarning]), ...parsed.warnings])
      };
    }
  };
  return Object.freeze(adapter);
}

export const FUNCTIONAL_ADAPTERS: readonly ServiceAdapter[] = Object.freeze([
  createLlamaAdapter(),
  createAudioCppAdapter(),
  createWhisperAdapter(),
  createSimilarityAdapter("minilm-l6"),
  createSimilarityAdapter("mpnet-base-v2")
]);
