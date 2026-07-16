import { SERVICE_ADAPTERS, type ServiceAdapter as ReadinessAdapter } from "./readiness";
import { LlamaPreparationError, parseLlamaSse, type LlamaModelOption, type LlamaModelState, type LlamaModelPreparer } from "./llama";
import { audioFilename, describeWav, isAudioMediaType, isDocumentedAudioFile, parseWav } from "./tts";

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

export const TURBO_TAGS: readonly string[] = Object.freeze([
  "[laugh]", "[chuckle]", "[sigh]", "[cough]", "[clear throat]", "[gasp]", "[groan]", "[sniff]", "[shush]"
]);
export const TURBO_TAG_EXAMPLE = "That went better than I expected. [sigh] Let us try the next chapter.";
const QWEN_LANGUAGES: readonly string[] = Object.freeze(["auto", "en", "zh", "ja", "ko", "de", "fr", "ru", "pt", "es", "it"]);
const QWEN_LANGUAGE_LABELS: Readonly<Record<string, string>> = Object.freeze({
  auto: "Automatic", en: "English", zh: "Chinese", ja: "Japanese", ko: "Korean", de: "German",
  fr: "French", ru: "Russian", pt: "Portuguese", es: "Spanish", it: "Italian"
});
const AUDIO_ACCEPT = ".wav,.mp3,audio/wav,audio/x-wav,audio/mpeg";
const ZERO_TO_ONE_GUIDED_FIELDS: readonly string[] = Object.freeze(["exaggeration", "cfg_weight"]);
const SPEECH_EXAMPLE = "It was a bright cold day in April, and the clocks were striking thirteen.";

function speechTextField(help: string, example: string): FieldDefinition {
  return Object.freeze({ key: "text", wireKey: "text", label: "Text to speak", control: "textarea", group: "common", required: true, initialValue: "", example, help });
}

function referenceAudioField(): FieldDefinition {
  return Object.freeze({
    key: "reference_audio", wireKey: "reference_audio", label: "Reference audio", control: "file", group: "common",
    required: true, initialValue: null, accept: AUDIO_ACCEPT,
    help: "Required voice-cloning clip. WAV or MP3 is documented; other decodable formats warn but are still sent."
  });
}

function qwenLanguageField(): FieldDefinition {
  return Object.freeze({
    key: "language", wireKey: "language", label: "Language", control: "select", group: "common", required: true, initialValue: "auto",
    help: "Sent explicitly on every request.",
    options: Object.freeze(QWEN_LANGUAGES.map((value) => Object.freeze({ value, label: `${value} — ${QWEN_LANGUAGE_LABELS[value] ?? value}` })))
  });
}

/**
 * Every TTS sampling control is optional on the wire: a prefilled value is sent because it is
 * present, and a cleared one is omitted rather than blocked or serialized as empty.
 */
function advancedNumber(key: string, label: string, initialValue: string, step: string, help: string): FieldDefinition {
  return Object.freeze({ key, wireKey: key, label, control: "number", group: "advanced", required: false, initialValue, step, help });
}

const OMITTED_HELP = "Optional. Left blank it is omitted from the request and the service default applies.";

function qwenSamplingFields(): readonly FieldDefinition[] {
  return Object.freeze([
    advancedNumber("temperature", "Temperature", "", "0.1", OMITTED_HELP),
    advancedNumber("top_p", "Top P", "", "0.05", OMITTED_HELP),
    advancedNumber("top_k", "Top K", "", "1", OMITTED_HELP),
    advancedNumber("repetition_penalty", "Repetition penalty", "", "0.1", OMITTED_HELP),
    advancedNumber("max_new_tokens", "Max new tokens", "", "1", OMITTED_HELP)
  ]);
}

export type TtsServiceId = "chatterbox" | "chatterbox-turbo" | "qwen3-tts" | "qwen3-tts-base";

interface TtsConfig {
  readonly path: string;
  readonly fields: readonly FieldDefinition[];
}

const TTS_CONFIGS: Readonly<Record<TtsServiceId, TtsConfig>> = Object.freeze({
  chatterbox: Object.freeze({
    path: "/tts",
    fields: Object.freeze([
      speechTextField("Plain text. This service takes expression from the reference clip and the Advanced controls, not from inline tags.", SPEECH_EXAMPLE),
      referenceAudioField(),
      advancedNumber("exaggeration", "Exaggeration", "0.5", "0.1", "Documented range is 0 to 1; the service does not enforce it."),
      advancedNumber("cfg_weight", "CFG weight", "0.5", "0.1", "Documented range is 0 to 1; the service does not enforce it."),
      advancedNumber("temperature", "Temperature", "0.8", "0.1", "Sampling randomness."),
      advancedNumber("min_p", "Min P", "0.05", "0.01", "Sampling floor."),
      advancedNumber("top_p", "Top P", "1.0", "0.05", "Sampling ceiling."),
      advancedNumber("repetition_penalty", "Repetition penalty", "1.2", "0.1", "Repetition penalty.")
    ])
  }),
  "chatterbox-turbo": Object.freeze({
    path: "/tts/turbo",
    fields: Object.freeze([
      speechTextField(`English text. Supported paralinguistic tags: ${TURBO_TAGS.join(", ")}.`, TURBO_TAG_EXAMPLE),
      referenceAudioField(),
      advancedNumber("temperature", "Temperature", "0.8", "0.1", "Sampling randomness."),
      advancedNumber("repetition_penalty", "Repetition penalty", "1.2", "0.1", "Repetition penalty.")
    ])
  }),
  "qwen3-tts": Object.freeze({
    path: "/tts",
    fields: Object.freeze([
      speechTextField("Text to speak in the described voice.", SPEECH_EXAMPLE),
      Object.freeze({
        key: "voice_description", wireKey: "voice_description", label: "Voice description", control: "textarea", group: "common",
        required: true, initialValue: "", help: "Describes the voice to design. No reference audio is used.",
        example: "A calm middle-aged British narrator with a warm, measured delivery."
      }),
      qwenLanguageField(),
      ...qwenSamplingFields()
    ])
  }),
  "qwen3-tts-base": Object.freeze({
    path: "/tts",
    fields: Object.freeze([
      speechTextField("Text to speak in the cloned voice.", SPEECH_EXAMPLE),
      referenceAudioField(),
      Object.freeze({
        key: "voice_transcript", wireKey: "voice_transcript", label: "Reference transcript", control: "textarea", group: "common",
        required: true, initialValue: "", help: "The exact words spoken in the reference clip.",
        example: "The quick brown fox jumps over the lazy dog."
      }),
      qwenLanguageField(),
      ...qwenSamplingFields()
    ])
  })
});

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

function requireReadinessMetadata(id: "llama" | "minilm-l6" | "mpnet-base-v2" | TtsServiceId) {
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
          // Llama reports failures through the OpenAI-style error envelope.
          extractMessage: (value) => typeof (value as { error?: { message?: unknown } })?.error?.message === "string"
            ? (value as { error: { message: string } }).error.message : safeServiceMessage(value)
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

function validateTts(fields: readonly FieldDefinition[], values: FormValues): ValidationResult {
  const errors: Record<string, string | undefined> = {};
  const warnings: Record<string, string | undefined> = {};
  for (const field of fields) {
    const value = values[field.key] ?? null;
    if (field.control === "file") {
      if (!(value instanceof File) || value.size === 0) errors[field.key] = "Choose a reference audio file.";
      else if (!isDocumentedAudioFile(value)) warnings[field.key] = "WAV and MP3 are the documented inputs; this file is still sent because the service decoder may accept it.";
      continue;
    }
    const text = typeof value === "string" ? value : "";
    if (field.control === "textarea" || field.control === "text") {
      errors[field.key] = field.required && text.trim() === "" ? `Enter ${field.label.toLowerCase()}.` : undefined;
      continue;
    }
    if (field.control === "select") {
      errors[field.key] = (field.options ?? []).some((option) => option.value === text) ? undefined : "Choose a supported option.";
      continue;
    }
    if (field.control !== "number") continue;
    if (text.trim() === "") {
      errors[field.key] = field.required ? "Enter a finite number." : undefined;
      continue;
    }
    // Only known-invalid shapes block; the services enforce no numeric bounds, so none are invented.
    errors[field.key] = isIntegerField(field)
      ? (/^-?\d+$/u.test(text.trim()) ? undefined : "Enter a whole number.")
      : finiteNumberError(text);
    if (errors[field.key] === undefined && ZERO_TO_ONE_GUIDED_FIELDS.includes(field.key) && (Number(text) < 0 || Number(text) > 1)) {
      warnings[field.key] = "The documented range is 0 to 1; the service does not enforce it, so this value is still sent.";
    }
  }
  return { errors: Object.freeze(errors), warnings: Object.freeze(warnings) };
}

function buildTtsRequest(fields: readonly FieldDefinition[], values: FormValues): { readonly form: FormData; readonly summary: string } {
  const form = new FormData();
  const lines: string[] = [];
  for (const field of fields) {
    const value = values[field.key] ?? null;
    if (value instanceof File) {
      form.append(field.wireKey, value, value.name);
      lines.push(`${field.wireKey}: ${value.name} · ${value.size} bytes · ${value.type || "unknown MIME type"}`);
      continue;
    }
    if (typeof value !== "string") continue;
    if (!field.required && value.trim() === "") continue;
    form.append(field.wireKey, value);
    lines.push(`${field.wireKey}: ${boundText(value, INPUT_TEXT_LIMIT)}`);
  }
  return { form, summary: lines.join("\n") };
}

export function createTtsAdapter(id: TtsServiceId): ServiceAdapter {
  const service = requireReadinessMetadata(id);
  const config = TTS_CONFIGS[id];
  const route = `/proxy/${id}${config.path}`;
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "audio",
    runLabel: "Generate speech",
    fields: config.fields,
    initialValues: () => initialValues(config.fields),
    validate: (values) => validateTts(config.fields, values),
    summarizeInput: (values) => summarize(config.fields, values),
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const { form, summary } = buildTtsRequest(config.fields, values);
      const requestDiagnostic = boundedWireDiagnostic(`Request:\nPOST ${route}\nContent-Type: multipart/form-data\n${summary}`);
      progress({ kind: "phase", phase: "request", status: "started", message: "Sending the speech request." });
      let response: Response;
      try {
        response = await fetcher(route, { method: "POST", headers: { accept: "audio/wav" }, body: form, signal });
      } catch (error) {
        if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
        throw unreachableFailure(service.name, error, requestDiagnostic);
      }
      progress({ kind: "phase", phase: "request", status: "completed", message: "The service accepted the request." });
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? "missing";
      const responseHeading = `${requestDiagnostic}\nResponse:\nHTTP ${response.status}\nContent-Type: ${mediaType}`;
      if (!response.ok) {
        const body = await boundedResponseText(response);
        let payload: unknown;
        if (isJson(response) && !body.endsWith(TRUNCATED)) {
          try { payload = JSON.parse(body); } catch { payload = undefined; }
        }
        throw reachedFailure({ response, payload, serviceName: service.name, diagnostic: boundedWireDiagnostic(`${responseHeading}\n${body}`) });
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
          filename: audioFilename(id), sampleRate: parsed.format.sampleRate
        },
        diagnostic: boundedWireDiagnostic(`${responseHeading}\nBytes: ${bytes.byteLength}\n${describeWav(parsed.format)}`),
        warnings: Object.freeze([])
      };
    }
  };
  return Object.freeze(adapter);
}

export const FUNCTIONAL_ADAPTERS: readonly ServiceAdapter[] = Object.freeze([
  createLlamaAdapter(),
  createTtsAdapter("chatterbox"),
  createTtsAdapter("chatterbox-turbo"),
  createTtsAdapter("qwen3-tts"),
  createTtsAdapter("qwen3-tts-base"),
  createSimilarityAdapter("minilm-l6"),
  createSimilarityAdapter("mpnet-base-v2")
]);
