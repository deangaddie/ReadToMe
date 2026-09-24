/**
 * Presentational event shapes the `r2m-stream-llm` / `r2m-stream-audio` components render
 * (ticket 03), mirrored from the in-process C# event records. They are NOT the hub wire shapes:
 * `/hubs/live` batches LLM deltas into one `{ kind: 'delta', thinking, content }` message (see
 * `live-messages.ts`, ticket 07). Ticket 14 maps the wire `llm` / `audioGen` families onto these.
 *
 * Source records: `Read2Me.Services/Llm/LlmStreamBroadcaster.cs`, `Read2Me.Services/Audio/AudioGenBroadcaster.cs`.
 */

// ---- LLM stream (research/live-events.md §1.1) --------------------------------------------------

export type LlmStreamEvent =
  | { kind: 'runStarted' }
  | { kind: 'runEnded' }
  | {
      kind: 'requestStarted';
      paragraphPreview: string;
      prompt: string;
      configId: string;
      configName: string;
    }
  | { kind: 'thinkingDelta'; text: string }
  | { kind: 'contentDelta'; text: string }
  | {
      kind: 'streamCompleted';
      tokensIn?: number | null;
      tokensOut?: number | null;
      generationMs?: number | null;
      tokensPerSecond?: number | null;
    }
  | { kind: 'streamFailed'; reason: string }
  | {
      kind: 'streamAborted';
      tokensOut?: number | null;
      generationMs?: number | null;
      tokensPerSecond?: number | null;
    }
  | { kind: 'escalationStarted'; step: number; configName: string; itemCount: number };

export type LlmStreamEventKind = LlmStreamEvent['kind'];

// ---- Audio generation stream (research/live-events.md §1.3) -------------------------------------

export type AudioGenEvent =
  | {
      kind: 'itemStarted';
      id: string;
      attempt: number;
      character?: string | null;
      text?: string | null;
    }
  | { kind: 'audioGenerated'; id: string; attempt: number }
  | { kind: 'normalized'; id: string; attempt: number; ok: boolean; reason?: string | null }
  | {
      kind: 'postProcessed';
      id: string;
      attempt: number;
      stepId: string;
      applied: boolean;
      reason?: string | null;
    }
  | { kind: 'transcribed'; id: string; attempt: number; transcript: string }
  | {
      kind: 'verified';
      id: string;
      attempt: number;
      ok: boolean;
      wer?: number | null;
      reason?: string | null;
      rescued: boolean;
    }
  | { kind: 'failed'; id: string; attempt: number; reason: string };

export type AudioGenEventKind = AudioGenEvent['kind'];
