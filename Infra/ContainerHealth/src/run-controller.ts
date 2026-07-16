import { ServiceFailure, type AudioResult, type FailureCategory, type InputSummaryItem, type ProgressEvent, type ServiceAdapter, type ServiceResult, type FormValues } from "./service-adapter";

export type RunStatus = "idle" | "running" | "succeeded" | "failed" | "cancelled";
export type RunOutcome = Exclude<RunStatus, "idle" | "running">;

export interface ResourceOwner {
  create(blob: Blob): string;
  revoke(url: string): void;
  stopAll(): void;
}

const browserResources: ResourceOwner = {
  create: (blob) => URL.createObjectURL(blob),
  revoke: (url) => URL.revokeObjectURL(url),
  stopAll: () => {
    if (typeof document !== "undefined") document.querySelectorAll<HTMLAudioElement>("audio").forEach((audio) => { audio.pause(); audio.removeAttribute("src"); audio.load(); });
  }
};

export interface RunEntry {
  readonly id: number;
  readonly outcome: RunOutcome;
  readonly startedAt: string;
  readonly finishedAt: string;
  readonly elapsedMs: number;
  readonly input: readonly InputSummaryItem[];
  readonly result?: ServiceResult;
  readonly resultUrl?: string;
  readonly warnings: readonly string[];
  readonly failureCategory?: FailureCategory | "internal";
  readonly title: string;
  readonly message: string;
  readonly diagnostic: string;
}

export interface RunSnapshot {
  readonly status: RunStatus;
  readonly activeRunId: number | undefined;
  readonly startedAtMs: number | undefined;
  readonly progress: ProgressEvent | undefined;
  readonly liveLlm: Readonly<{ thinking: string; answer: string }>;
  readonly validationErrors: Readonly<Record<string, string | undefined>>;
  readonly validationWarnings: Readonly<Record<string, string | undefined>>;
  readonly history: readonly RunEntry[];
}

export type RunUpdate = (snapshot: RunSnapshot) => void;

interface ActiveRun { readonly id: number; readonly controller: AbortController; readonly startedAt: Date; readonly input: readonly InputSummaryItem[] }

function failurePresentation(adapter: ServiceAdapter, error: ServiceFailure): Pick<RunEntry, "title" | "message"> {
  if (error.category === "unavailable") return { title: "Unavailable", message: `The dashboard could not reach ${adapter.name} through the local proxy.` };
  if (error.category === "http") return { title: "HTTP failure", message: `HTTP ${error.status ?? "error"}${error.serviceMessage ? ` — ${error.serviceMessage}` : ""}` };
  return { title: "Protocol failure", message: error.message };
}

export class DetailRunController {
  private status: RunStatus = "idle";
  private active: ActiveRun | undefined;
  private nextRunId = 0;
  private history: RunEntry[] = [];
  private errors: Readonly<Record<string, string | undefined>> = Object.freeze({});
  private warnings: Readonly<Record<string, string | undefined>> = Object.freeze({});
  private progress: ProgressEvent | undefined;
  private liveLlm = { thinking: "", answer: "" };
  private disposed = false;
  private playingId: number | undefined;
  private revoked = new Set<string>();

  constructor(
    private readonly adapter: ServiceAdapter,
    private readonly update: RunUpdate = () => {},
    private readonly resources: ResourceOwner = browserResources,
    private readonly now: () => Date = () => new Date()
  ) {}

  get snapshot(): RunSnapshot {
    return Object.freeze({
      status: this.status,
      activeRunId: this.active?.id,
      startedAtMs: this.active?.startedAt.getTime(),
      progress: this.progress,
      liveLlm: Object.freeze({ ...this.liveLlm }),
      validationErrors: this.errors,
      validationWarnings: this.warnings,
      history: Object.freeze([...this.history])
    });
  }

  private publish(): void { if (!this.disposed) this.update(this.snapshot); }

  async submit(values: FormValues): Promise<"started" | "busy" | "invalid"> {
    if (this.disposed || this.active !== undefined) return "busy";
    const validation = this.adapter.validate(values);
    this.errors = validation.errors;
    this.warnings = validation.warnings;
    if (Object.values(validation.errors).some((message) => message !== undefined)) {
      this.publish();
      return "invalid";
    }
    const active: ActiveRun = { id: ++this.nextRunId, controller: new AbortController(), startedAt: this.now(), input: this.adapter.summarizeInput(values) };
    this.active = active;
    this.status = "running";
    this.progress = undefined;
    this.liveLlm = { thinking: "", answer: "" };
    this.publish();
    try {
      const execution = await this.adapter.execute(values, active.controller.signal, (event) => {
        if (this.isActive(active.id)) {
          this.progress = event;
          if (event.kind === "thinking-delta") this.liveLlm.thinking += event.text;
          if (event.kind === "answer-delta") this.liveLlm.answer += event.text;
          this.publish();
        }
      });
      if (this.claim(active.id, "succeeded")) {
        const resultUrl = execution.result.kind === "audio" ? this.resources.create((execution.result as AudioResult).blob) : undefined;
        this.insert({ active, outcome: "succeeded", result: execution.result, warnings: execution.warnings, title: "Succeeded", message: "The service test completed successfully.", diagnostic: execution.diagnostic, ...(resultUrl === undefined ? {} : { resultUrl }) });
      }
    } catch (error) {
      if (!this.isActive(active.id)) return "started";
      if (active.controller.signal.aborted || (error instanceof DOMException && error.name === "AbortError")) return "started";
      if (error instanceof ServiceFailure) {
        if (this.claim(active.id, "failed")) {
          const presentation = failurePresentation(this.adapter, error);
          this.insert({ active, outcome: "failed", warnings: [], failureCategory: error.category, diagnostic: error.diagnostic, ...presentation });
        }
      } else if (this.claim(active.id, "failed")) {
        console.error("Internal dashboard run error", error);
        this.insert({ active, outcome: "failed", warnings: [], failureCategory: "internal", title: "Internal dashboard error", message: "The dashboard could not process this run. See the browser console for details.", diagnostic: error instanceof Error ? `${error.name}: ${error.message}` : "Unexpected error." });
      }
    }
    return "started";
  }

  cancel(): boolean {
    const active = this.active;
    if (active === undefined || !this.claim(active.id, "cancelled")) return false;
    active.controller.abort();
    this.insert({
      active, outcome: "cancelled", warnings: [], title: "Cancelled",
      message: "Cancelled by you. The service may continue processing after the connection closes.", diagnostic: "Run cancelled by the operator."
    });
    return true;
  }

  setPlaying(runId: number | undefined): void {
    this.playingId = runId;
  }

  private isActive(id: number): boolean { return !this.disposed && this.active?.id === id && this.status === "running"; }

  private claim(id: number, status: RunOutcome): boolean {
    if (!this.isActive(id)) return false;
    this.status = status;
    this.active = undefined;
    this.progress = undefined;
    return true;
  }

  private insert(entry: {
    active: ActiveRun; outcome: RunOutcome; result?: ServiceResult; resultUrl?: string; warnings: readonly string[];
    failureCategory?: FailureCategory | "internal"; title: string; message: string; diagnostic: string;
  }): void {
    const finished = this.now();
    const runEntry: RunEntry = Object.freeze({
      id: entry.active.id, outcome: entry.outcome, startedAt: entry.active.startedAt.toISOString(), finishedAt: finished.toISOString(),
      elapsedMs: Math.max(0, finished.getTime() - entry.active.startedAt.getTime()), input: entry.active.input,
      warnings: Object.freeze([...entry.warnings]), title: entry.title, message: entry.message, diagnostic: entry.diagnostic,
      ...(entry.result === undefined ? {} : { result: entry.result }),
      ...(entry.resultUrl === undefined ? {} : { resultUrl: entry.resultUrl }),
      ...(entry.failureCategory === undefined ? {} : { failureCategory: entry.failureCategory })
    });
    this.history.unshift(runEntry);
    while (this.history.length > 5) {
      let index = this.history.length - 1;
      while (index >= 0 && this.history[index]?.id === this.playingId) index -= 1;
      const removed = this.history.splice(index, 1)[0];
      if (removed?.resultUrl !== undefined) this.revoke(removed.resultUrl);
    }
    this.publish();
  }

  private revoke(url: string): void {
    if (this.revoked.has(url)) return;
    this.revoked.add(url);
    this.resources.revoke(url);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.active?.controller.abort();
    this.active = undefined;
    this.resources.stopAll();
    for (const entry of this.history) if (entry.resultUrl !== undefined) this.revoke(entry.resultUrl);
    this.history = [];
  }
}
