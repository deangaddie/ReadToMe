import { SERVICE_ADAPTERS, type ServiceAdapter as ReadinessAdapter } from "./readiness";
import { LlamaPreparationError, parseLlamaSse, type LlamaModelOption, type LlamaModelState, type LlamaModelPreparer } from "./llama";
import { audioFilename, describeWav, isAudioMediaType, isDocumentedAudioFile, parseWav } from "./tts";
import { buildPcm16Wav, isSupportedVoxUpload, parseVoxStream, VOX_UPLOAD_EXTENSIONS, VOX_UPLOAD_LIMIT_BYTES, VOX_UPLOAD_LIMIT_MIB } from "./vox";
import { inspectUpload, parseTranscription, TRANSCRIPTION_FORMATS, type TranscriptionFormat } from "./whisper";

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

function requireReadinessMetadata(id: "llama" | "minilm-l6" | "mpnet-base-v2" | "voxcpm2" | "whisper" | TtsServiceId) {
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
    errors[field.key] = fieldValueError(field, value);
    if (field.control === "number" && text.trim() !== "" && errors[field.key] === undefined
      && ZERO_TO_ONE_GUIDED_FIELDS.includes(field.key) && (Number(text) < 0 || Number(text) > 1)) {
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

const VOX_ACCEPT = VOX_UPLOAD_EXTENSIONS.join(",");
const VOX_UPLOAD_ROUTE = "/proxy/voxcpm2/upload-audio";
const VOX_STREAM_ROUTE = "/proxy/voxcpm2/api/stream";

const voxFields = Object.freeze([
  speechTextField("Text to speak in the uploaded reference voice.", SPEECH_EXAMPLE),
  Object.freeze({
    key: "reference_audio", wireKey: "file", label: "Reference audio", control: "file", group: "common",
    required: true, initialValue: null, accept: VOX_ACCEPT,
    help: `Required voice-cloning clip, uploaded fresh for every run. ${VOX_UPLOAD_EXTENSIONS.join(", ")} only, up to ${VOX_UPLOAD_LIMIT_MIB} MiB.`
  }),
  Object.freeze({
    key: "control", wireKey: "control", label: "Control", control: "text", group: "common", required: false, initialValue: "",
    help: "Optional. Prepended to the text as (control) to steer delivery.", example: "whispering"
  }),
  advancedNumber("cfg_value", "CFG value", "2.0", "0.1", "Guidance strength."),
  advancedNumber("inference_timesteps", "Inference timesteps", "10", "1", "Diffusion steps per chunk."),
  advancedNumber("min_len", "Minimum length", "2", "1", "Must not exceed the maximum length."),
  advancedNumber("max_len", "Maximum length", "4096", "1", "Must not be below the minimum length."),
  Object.freeze({ key: "normalize", wireKey: "normalize", label: "Normalize text", control: "checkbox", group: "advanced", required: false, initialValue: false, help: "Sent explicitly on every request." }),
  Object.freeze({ key: "denoise", wireKey: "denoise", label: "Denoise reference", control: "checkbox", group: "advanced", required: false, initialValue: false, help: "Sent explicitly on every request." }),
  Object.freeze({ key: "retry_badcase", wireKey: "retry_badcase", label: "Retry bad cases", control: "checkbox", group: "advanced", required: false, initialValue: true, help: "Sent explicitly on every request." }),
  advancedNumber("retry_badcase_max_times", "Retry maximum", "3", "1", "Maximum bad-case retries."),
  advancedNumber("retry_badcase_ratio_threshold", "Retry ratio threshold", "6.0", "0.1", "Bad-case detection ratio.")
] satisfies readonly FieldDefinition[]);

function validateVox(values: FormValues): ValidationResult {
  const errors: Record<string, string | undefined> = {};
  const warnings: Record<string, string | undefined> = {};
  for (const field of voxFields) {
    const value = values[field.key] ?? null;
    if (field.control === "checkbox") continue;
    if (field.control === "file") {
      // Unlike the permissive Chatterbox/Qwen decoders, this route rejects by extension and size itself.
      if (!(value instanceof File) || value.size === 0) errors[field.key] = "Choose a reference audio file.";
      else if (!isSupportedVoxUpload(value)) errors[field.key] = `Choose a ${VOX_UPLOAD_EXTENSIONS.join(", ")} file.`;
      else if (value.size > VOX_UPLOAD_LIMIT_BYTES) errors[field.key] = `Choose a reference audio file of ${VOX_UPLOAD_LIMIT_MIB} MiB or less.`;
      continue;
    }
    errors[field.key] = fieldValueError(field, value);
  }
  const min = stringValue(values, "min_len") ?? "";
  const max = stringValue(values, "max_len") ?? "";
  if (errors.min_len === undefined && errors.max_len === undefined && min.trim() !== "" && max.trim() !== "" && Number(min) > Number(max)) {
    errors.min_len = "The minimum length cannot exceed the maximum length.";
  }
  return { errors: Object.freeze(errors), warnings: Object.freeze(warnings) };
}

function buildVoxRequest(values: FormValues, fileId: string): Record<string, unknown> {
  const request: Record<string, unknown> = {};
  for (const field of voxFields) {
    const value = values[field.key] ?? null;
    if (field.control === "file") continue;
    if (field.control === "checkbox") { request[field.wireKey] = value === true; continue; }
    if (typeof value !== "string") continue;
    if (value.trim() === "") { if (field.required) request[field.wireKey] = value; continue; }
    request[field.wireKey] = field.control === "number" ? Number(value) : value;
  }
  request.reference_wav_path = fileId;
  return request;
}

/** Uploads the reference clip for this run only; the returned identifier is used immediately and never cached. */
async function uploadVoxReference(file: File, serviceName: string, signal: AbortSignal, fetcher: typeof fetch, progress: ProgressEmitter): Promise<{ readonly fileId: string; readonly diagnostic: string }> {
  const form = new FormData();
  form.append("file", file, file.name);
  const requestDiagnostic = `Upload:\nPOST ${VOX_UPLOAD_ROUTE}\nfile: ${file.name} · ${file.size} bytes · ${file.type || "unknown MIME type"}`;
  progress({ kind: "phase", phase: "upload", status: "started", message: "Uploading the reference audio." });
  let response: Response;
  try {
    response = await fetcher(VOX_UPLOAD_ROUTE, { method: "POST", headers: { accept: "application/json" }, body: form, signal });
  } catch (error) {
    if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw error;
    throw unreachableFailure(serviceName, error, requestDiagnostic);
  }
  const body = await boundedResponseText(response);
  let payload: unknown;
  if (isJson(response) && !body.endsWith(TRUNCATED)) {
    try { payload = JSON.parse(body); } catch { payload = undefined; }
  }
  const diagnostic = boundedWireDiagnostic(`${requestDiagnostic}\nResponse: HTTP ${response.status}\n${body}`);
  if (!response.ok) throw reachedFailure({ response, payload, serviceName, diagnostic });
  const fileId = isRecord(payload) ? payload.file_id : undefined;
  if (typeof fileId !== "string" || fileId.trim() === "") {
    throw new ServiceFailure({ category: "protocol", message: "The upload response did not contain a file identifier.", diagnostic });
  }
  progress({ kind: "phase", phase: "upload", status: "completed", message: "Reference audio uploaded." });
  return { fileId, diagnostic };
}

export function createVoxAdapter(): ServiceAdapter {
  const service = requireReadinessMetadata("voxcpm2");
  const adapter: ServiceAdapter = {
    ...service,
    resultKind: "audio",
    runLabel: "Generate speech",
    fields: voxFields,
    initialValues: () => initialValues(voxFields),
    validate: validateVox,
    summarizeInput: (values) => summarize(voxFields, values),
    async execute(values, signal, progress, fetcher = fetch): Promise<AdapterExecution> {
      const file = values.reference_audio;
      if (!(file instanceof File)) throw new Error("VoxCPM2 execution received invalid values.");
      const upload = await uploadVoxReference(file, service.name, signal, fetcher, progress);
      const request = buildVoxRequest(values, upload.fileId);
      const requestDiagnostic = boundedWireDiagnostic(`${upload.diagnostic}\nRequest:\nPOST ${VOX_STREAM_ROUTE}\n${JSON.stringify(request, null, 2)}`);
      let response: Response;
      try {
        response = await fetcher(VOX_STREAM_ROUTE, {
          method: "POST", headers: { accept: "application/octet-stream", "content-type": "application/json" },
          body: JSON.stringify(request), signal
        });
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
        throw reachedFailure({ response, payload, serviceName: service.name, diagnostic: boundedWireDiagnostic(`${responseHeading}\n${body}`) });
      }
      if (mediaType !== "application/octet-stream" || response.body === null) {
        const body = response.body === null ? "" : await boundedResponseText(response);
        throw new ServiceFailure({ category: "protocol", message: "The service returned a success response that is not a framed audio stream.", diagnostic: boundedWireDiagnostic(`${responseHeading}\n${body}`) });
      }
      let parsed;
      progress({ kind: "phase", phase: "generate", status: "started", message: "Receiving the generated audio stream." });
      try {
        parsed = await parseVoxStream(response.body, signal);
      } catch (error) {
        if (error instanceof ServiceFailure) throw new ServiceFailure({
          category: error.category, message: error.message, status: error.status, serviceMessage: error.serviceMessage,
          diagnostic: boundedWireDiagnostic(`${responseHeading}\nStream:\n${error.diagnostic}`), partialResult: error.partialResult
        });
        throw error;
      }
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      progress({ kind: "phase", phase: "generate", status: "completed", message: "Generated audio stream received." });
      // The Blob is created only after conversion and validation both succeed, so no partial audio escapes.
      progress({ kind: "phase", phase: "convert", status: "started", message: "Converting samples to WAV audio." });
      const bytes = buildPcm16Wav(parsed.samples, parsed.sampleRate);
      const wav = parseWav(bytes);
      if (!wav.ok) throw new ServiceFailure({ category: "protocol", message: `The converted audio is not valid WAV audio. ${wav.reason}`, diagnostic: boundedWireDiagnostic(`${responseHeading}\nStream:\n${parsed.diagnostic}\n${wav.reason}`) });
      progress({ kind: "phase", phase: "convert", status: "completed", message: "WAV audio ready." });
      return {
        result: {
          kind: "audio", blob: new Blob([bytes], { type: "audio/wav" }), mediaType: "audio/wav",
          filename: audioFilename("voxcpm2"), sampleRate: parsed.sampleRate
        },
        diagnostic: boundedWireDiagnostic(`${responseHeading}\nStream:\n${parsed.diagnostic}\nBytes: ${bytes.byteLength}\n${describeWav(wav.format)}`),
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
  whisperNumber("Voice activity detection", "vad_max_speech_duration_s", "VAD maximum speech (s)", "3.402823466e38", "0.1", "Longest accepted speech run."),
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
      const uploadWarning = await inspectUpload(file);
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
  createTtsAdapter("chatterbox"),
  createTtsAdapter("chatterbox-turbo"),
  createTtsAdapter("qwen3-tts"),
  createTtsAdapter("qwen3-tts-base"),
  createVoxAdapter(),
  createWhisperAdapter(),
  createSimilarityAdapter("minilm-l6"),
  createSimilarityAdapter("mpnet-base-v2")
]);
