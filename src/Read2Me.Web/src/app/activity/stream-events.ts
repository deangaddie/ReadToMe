import { AudioGenEvent, LlmStreamEvent } from '@app/live/hub-events';
import { AudioGenMessage, LlmMessage } from '@app/live/live-messages';

/**
 * Wire → presentational mapping for the two stream tabs (ticket 14). `/hubs/live` batches LLM
 * token deltas into one `delta` message per ~100 ms; the `r2m-stream-llm` component still thinks in
 * per-kind deltas, so a batch becomes up to two events. Audio phases map one to one.
 */
export function mapLlmMessage(m: LlmMessage): LlmStreamEvent[] {
  switch (m.kind) {
    case 'delta': {
      const events: LlmStreamEvent[] = [];
      if (m.thinking) events.push({ kind: 'thinkingDelta', text: m.thinking });
      if (m.content) events.push({ kind: 'contentDelta', text: m.content });
      return events;
    }
    case 'requestStarted':
      return [
        {
          kind: 'requestStarted',
          paragraphPreview: m.paragraphPreview ?? '',
          prompt: m.prompt ?? '',
          configId: m.configId == null ? '' : String(m.configId),
          configName: m.configName ?? '',
        },
      ];
    case 'streamCompleted':
      return [
        {
          kind: 'streamCompleted',
          tokensIn: m.tokensIn ?? null,
          tokensOut: m.tokensOut ?? null,
          generationMs: m.generationMs ?? null,
          tokensPerSecond: m.tokensPerSecond ?? null,
        },
      ];
    case 'streamFailed':
      return [{ kind: 'streamFailed', reason: m.reason ?? '' }];
    case 'streamAborted':
      return [
        {
          kind: 'streamAborted',
          tokensOut: m.tokensOut ?? null,
          generationMs: m.generationMs ?? null,
          tokensPerSecond: m.tokensPerSecond ?? null,
        },
      ];
    case 'escalationStarted':
      return [
        {
          kind: 'escalationStarted',
          step: m.step ?? 0,
          configName: m.configName ?? '',
          itemCount: m.itemCount ?? 0,
        },
      ];
    case 'runStarted':
      return [{ kind: 'runStarted' }];
    case 'runEnded':
      return [{ kind: 'runEnded' }];
  }
}

export function mapAudioGenMessage(m: AudioGenMessage): AudioGenEvent {
  const base = { id: m.id, attempt: m.attempt };
  switch (m.kind) {
    case 'itemStarted':
      return { kind: 'itemStarted', ...base, character: m.character ?? null, text: m.text ?? null };
    case 'audioGenerated':
      return { kind: 'audioGenerated', ...base };
    case 'normalized':
      return { kind: 'normalized', ...base, ok: m.ok ?? false, reason: m.reason ?? null };
    case 'postProcessed':
      return {
        kind: 'postProcessed',
        ...base,
        stepId: m.stepId ?? '',
        applied: m.applied ?? false,
        reason: m.reason ?? null,
      };
    case 'transcribed':
      return { kind: 'transcribed', ...base, transcript: m.transcript ?? '' };
    case 'verified':
      return {
        kind: 'verified',
        ...base,
        ok: m.ok ?? false,
        wer: m.wer ?? null,
        reason: m.reason ?? null,
        rescued: m.rescued ?? false,
      };
    case 'failed':
      return { kind: 'failed', ...base, reason: m.reason ?? '' };
  }
}

export const startsLlmTurn = (e: LlmStreamEvent): boolean => e.kind === 'requestStarted';
export const startsAudioCard = (e: AudioGenEvent): boolean => e.kind === 'itemStarted';

/**
 * Appends events and trims the buffer to the last `max` units (turns or cards), where a unit begins
 * at an event `startsUnit` accepts. Events before the oldest retained unit go with it, so the
 * folded view never shows a headless tail.
 */
export function appendCapped<T>(
  events: readonly T[],
  incoming: readonly T[],
  startsUnit: (e: T) => boolean,
  max: number,
): T[] {
  const all = [...events, ...incoming];
  let units = 0;
  for (let i = all.length - 1; i >= 0; i--) {
    if (startsUnit(all[i]!) && ++units === max) return i === 0 ? all : all.slice(i);
  }
  return all;
}
