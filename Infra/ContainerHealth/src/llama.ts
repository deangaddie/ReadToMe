import { ServiceFailure, type ProgressEmitter } from "./service-adapter";

export type LlamaModelState = "loaded" | "loading" | "sleeping" | "unloaded" | "failed";

export interface LlamaModelOption {
  readonly id: string;
  readonly state: LlamaModelState;
  readonly label: string;
  readonly runnable: boolean;
}

export interface LlamaPreparationSnapshot {
  readonly status: "idle" | "preparing" | "prepared" | "failed";
  readonly models: readonly LlamaModelOption[];
  readonly selectedModel?: string;
  readonly diagnostic: string;
}

export interface PreparedLlamaModels {
  readonly models: readonly LlamaModelOption[];
  readonly diagnostic: string;
}

export type LlamaModelPreparer = (signal: AbortSignal, fetcher?: typeof fetch) => Promise<PreparedLlamaModels>;

export class LlamaPreparationError extends Error {
  constructor(readonly diagnostic: string) { super("Model preparation failed."); this.name = "LlamaPreparationError"; }
}

const idlePreparation: LlamaPreparationSnapshot = Object.freeze({ status: "idle", models: Object.freeze([]), diagnostic: "" });

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export class LlamaPreparationController {
  private current: LlamaPreparationSnapshot = idlePreparation;
  private epoch = 0;
  private controller: AbortController | undefined;

  constructor(private readonly prepareModels: LlamaModelPreparer) {}

  get snapshot(): LlamaPreparationSnapshot { return this.current; }

  select(id: string): void {
    if (this.current.status !== "prepared" || !this.current.models.some((model) => model.id === id && model.runnable)) return;
    this.current = Object.freeze({ ...this.current, selectedModel: id });
  }

  async refresh(fetcher: typeof fetch = fetch): Promise<LlamaPreparationSnapshot> {
    const epoch = ++this.epoch;
    this.controller?.abort();
    const controller = new AbortController();
    this.controller = controller;
    const previous = this.current.selectedModel;
    this.current = Object.freeze({ ...this.current, status: "preparing" });
    let next: LlamaPreparationSnapshot;
    try {
      const prepared = await this.prepareModels(controller.signal, fetcher);
      const selectedModel = previous !== undefined && prepared.models.some((model) => model.id === previous && model.runnable) ? previous
        : prepared.models.find((model) => model.state === "loaded")?.id ?? prepared.models.find((model) => model.runnable)?.id;
      next = Object.freeze({ status: "prepared", models: prepared.models, ...(selectedModel === undefined ? {} : { selectedModel }), diagnostic: prepared.diagnostic });
    } catch (error) {
      if (controller.signal.aborted && epoch !== this.epoch) return this.current;
      next = Object.freeze({ status: "failed", models: Object.freeze([]), diagnostic: error instanceof LlamaPreparationError ? error.diagnostic : error instanceof Error ? `${error.name}: ${error.message}` : "Model preparation failed." });
    }
    if (epoch === this.epoch) { this.current = next; this.controller = undefined; }
    return this.current;
  }

  invalidate(): void { this.epoch += 1; this.controller?.abort(); this.controller = undefined; }
}

export interface ParsedLlamaStream {
  readonly thinking: string;
  readonly answer: string;
  readonly finishReason?: string;
  readonly usage?: Readonly<Record<string, number>>;
  readonly timing?: Readonly<Record<string, number>>;
  readonly diagnostic: string;
}

function finiteNumbers(value: unknown): Readonly<Record<string, number>> | undefined {
  if (!isRecord(value)) return undefined;
  const entries = Object.entries(value).filter((entry): entry is [string, number] => typeof entry[1] === "number" && Number.isFinite(entry[1]));
  return entries.length === 0 ? undefined : Object.freeze(Object.fromEntries(entries));
}

export async function parseLlamaSse(stream: ReadableStream<Uint8Array>, signal: AbortSignal, emit: ProgressEmitter): Promise<ParsedLlamaStream> {
  if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let dataLines: string[] = [];
  let thinking = "";
  let answer = "";
  let finishReason: string | undefined;
  let usage: Readonly<Record<string, number>> | undefined;
  let timing: Readonly<Record<string, number>> | undefined;
  let done = false;
  const notes: string[] = [];
  let noteLength = 0;
  const addNote = (note: string): void => {
    const limit = 64 * 1_024;
    if (noteLength >= limit) return;
    const remaining = limit - noteLength;
    if (note.length + 1 > remaining) {
      notes.push(`${note.slice(0, Math.max(0, remaining - 13))}\n[truncated]`);
      noteLength = limit;
    } else {
      notes.push(note);
      noteLength += note.length + 1;
    }
  };
  const diagnostic = (): string => {
    const value = notes.join("\n");
    if (!done || value.includes("[DONE]")) return value;
    return `${value.slice(0, (64 * 1_024) - 20)}\n[truncated]\n[DONE]`;
  };
  const partialDiagnostic = (base: string): string => {
    const value = `${base}\nPartial thinking:\n${thinking}\nPartial answer:\n${answer}`;
    return value.length <= 64 * 1_024 ? value : `${value.slice(0, (64 * 1_024) - 13)}\n[truncated]`;
  };
  const onAbort = (): void => { void reader.cancel(); };
  signal.addEventListener("abort", onAbort, { once: true });

  const dispatch = (): void => {
    if (dataLines.length === 0) return;
    const data = dataLines.join("\n");
    dataLines = [];
    if (data.trim() === "[DONE]") { done = true; addNote("[DONE]"); return; }
    let payload: unknown;
    try { payload = JSON.parse(data); } catch {
      throw new ServiceFailure({ category: "protocol", message: "The service returned malformed streaming JSON.", diagnostic: partialDiagnostic(`Malformed SSE data: ${data.slice(0, 2_048)}`), partialResult: { thinking, answer } });
    }
    if (!isRecord(payload)) throw new ServiceFailure({ category: "protocol", message: "The service returned an invalid streaming envelope.", diagnostic: partialDiagnostic("Invalid SSE envelope."), partialResult: { thinking, answer } });
    if (isRecord(payload.error)) {
      const message = typeof payload.error.message === "string" ? payload.error.message : "The service returned a streaming error.";
      throw new ServiceFailure({ category: "http", message, serviceMessage: message, diagnostic: partialDiagnostic(JSON.stringify(payload)), partialResult: { thinking, answer } });
    }
    if (payload.choices !== undefined && !Array.isArray(payload.choices)) throw new ServiceFailure({ category: "protocol", message: "The service returned an invalid streaming envelope.", diagnostic: partialDiagnostic(JSON.stringify(payload).slice(0, 2_048)), partialResult: { thinking, answer } });
    const choice = Array.isArray(payload.choices) && isRecord(payload.choices[0]) ? payload.choices[0] : undefined;
    const delta = choice !== undefined && isRecord(choice.delta) ? choice.delta : undefined;
    const reasoning = typeof delta?.reasoning_content === "string" ? delta.reasoning_content : typeof delta?.reasoning === "string" ? delta.reasoning : "";
    const content = typeof delta?.content === "string" ? delta.content : "";
    if (reasoning !== "") { thinking += reasoning; emit({ kind: "thinking-delta", text: reasoning }); }
    if (content !== "") { answer += content; emit({ kind: "answer-delta", text: content }); }
    if (choice !== undefined && typeof choice.finish_reason === "string") finishReason = choice.finish_reason;
    usage = finiteNumbers(payload.usage) ?? usage;
    timing = finiteNumbers(payload.timings) ?? finiteNumbers(payload.timing) ?? timing;
    addNote(`event ${notes.length + 1}: thinking=${reasoning.length} answer=${content.length}${finishReason ? ` finish=${finishReason}` : ""}`);
  };

  const consumeLines = (flush = false): void => {
    while (true) {
      const match = /\r?\n/u.exec(buffer);
      if (match === null) break;
      const line = buffer.slice(0, match.index);
      buffer = buffer.slice(match.index + match[0].length);
      if (line === "") { dispatch(); if (done) { buffer = ""; return; } }
      else if (!line.startsWith(":")) {
        if (line.startsWith("data:")) dataLines.push(line.slice(5).replace(/^ /u, ""));
      }
    }
    if (flush && buffer !== "") { if (buffer.startsWith("data:")) dataLines.push(buffer.slice(5).replace(/^ /u, "")); buffer = ""; }
  };

  try {
    while (true) {
      const item = await reader.read();
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      if (item.done) break;
      buffer += decoder.decode(item.value, { stream: true });
      consumeLines();
      if (done) { await reader.cancel(); break; }
    }
    buffer += decoder.decode();
    consumeLines(true);
    dispatch();
    if (!done) throw new ServiceFailure({ category: "protocol", message: "The service response ended before completion.", diagnostic: partialDiagnostic(`${diagnostic()}\n[incomplete]`), partialResult: { thinking, answer } });
    return Object.freeze({ thinking, answer, ...(finishReason === undefined ? {} : { finishReason }), ...(usage === undefined ? {} : { usage }), ...(timing === undefined ? {} : { timing }), diagnostic: diagnostic() });
  } catch (error) {
    if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw new DOMException("The operation was aborted.", "AbortError");
    if (error instanceof ServiceFailure) throw error;
    throw new ServiceFailure({ category: "protocol", message: "The service response ended before completion.", diagnostic: partialDiagnostic(`${diagnostic()}\nStream read failed: ${error instanceof Error ? error.message : "unknown error"}`), partialResult: { thinking, answer } });
  } finally {
    signal.removeEventListener("abort", onAbort);
    reader.releaseLock();
  }
}
