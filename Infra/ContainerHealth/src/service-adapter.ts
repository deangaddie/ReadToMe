import { SERVICE_ADAPTERS, type ServiceAdapter as ReadinessAdapter } from "./readiness";

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
  readonly fields: readonly FieldDefinition[];
  initialValues(): FormValues;
  validate(values: FormValues): ValidationResult;
  summarizeInput(values: FormValues): readonly InputSummaryItem[];
  execute(values: FormValues, signal: AbortSignal, progress: ProgressEmitter, fetcher?: typeof fetch): Promise<AdapterExecution>;
}

const similarityFields = Object.freeze([
  Object.freeze({ key: "text1", wireKey: "text1", label: "First text", control: "textarea", group: "common", required: true, initialValue: "", example: "The quick brown fox jumps over the lazy dog." }),
  Object.freeze({ key: "text2", wireKey: "text2", label: "Second text", control: "textarea", group: "common", required: true, initialValue: "", example: "A fast brown fox leaps over a sleepy dog." })
] satisfies readonly FieldDefinition[]);

function boundText(value: string, limit: number): string {
  return value.length <= limit ? value : `${value.slice(0, limit)}${TRUNCATED}`;
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

function requireReadinessMetadata(id: "minilm-l6" | "mpnet-base-v2") {
  const value = SERVICE_ADAPTERS.find((adapter) => adapter.id === id);
  if (value === undefined) throw new Error(`Missing readiness metadata for ${id}.`);
  return value;
}

export function createSimilarityAdapter(id: "minilm-l6" | "mpnet-base-v2"): ServiceAdapter {
  const service = requireReadinessMetadata(id);
  const adapter: ServiceAdapter = {
    ...service,
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
  createSimilarityAdapter("minilm-l6"),
  createSimilarityAdapter("mpnet-base-v2")
]);
