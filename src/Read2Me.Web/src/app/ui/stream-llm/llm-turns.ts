import { LlmStreamEvent } from '@app/live/hub-events';

export type LlmTurnState = 'open' | 'completed' | 'failed' | 'aborted';

export interface LlmTurn {
  kind: 'turn';
  /** Stable key for tracking: sequence number of the requestStarted event. */
  seq: number;
  paragraphPreview: string;
  prompt: string;
  configName: string;
  thinking: string;
  response: string;
  state: LlmTurnState;
  reason?: string | null | undefined;
  tokensIn?: number | null | undefined;
  tokensOut?: number | null | undefined;
  generationMs?: number | null | undefined;
  tokensPerSecond?: number | null | undefined;
}

export interface LlmMarker {
  kind: 'marker';
  seq: number;
  text: string;
  icon: string;
}

export type LlmStreamRow = LlmTurn | LlmMarker;

/** The status dock's escalation text, shared by the stream marker and the attribution job card. */
export function escalationBanner(
  itemCount: number,
  configName: string | null | undefined,
  step: number,
): string {
  return `Escalating ${itemCount} ${itemCount === 1 ? 'item' : 'items'} → ${configName || '?'} (step ${step})`;
}

/**
 * Folds a flat event list into turn cards and marker rows (research/live-events.md §1.1). Deltas
 * append to the most recent open turn; completion events close it. Keeps the last `maxTurns` turns
 * (markers older than the oldest kept turn are dropped with it).
 */
export function foldLlmTurns(events: readonly LlmStreamEvent[], maxTurns = 50): LlmStreamRow[] {
  const rows: LlmStreamRow[] = [];
  let open: LlmTurn | null = null;

  events.forEach((e, seq) => {
    switch (e.kind) {
      case 'requestStarted':
        open = {
          kind: 'turn',
          seq,
          paragraphPreview: e.paragraphPreview,
          prompt: e.prompt,
          configName: e.configName,
          thinking: '',
          response: '',
          state: 'open',
        };
        rows.push(open);
        break;
      case 'thinkingDelta':
        if (open) open.thinking += e.text;
        break;
      case 'contentDelta':
        if (open) open.response += e.text;
        break;
      case 'streamCompleted':
        if (open) {
          Object.assign(open, {
            state: 'completed',
            tokensIn: e.tokensIn,
            tokensOut: e.tokensOut,
            generationMs: e.generationMs,
            tokensPerSecond: e.tokensPerSecond,
          });
          open = null;
        }
        break;
      case 'streamFailed':
        if (open) {
          Object.assign(open, { state: 'failed', reason: e.reason });
          open = null;
        }
        break;
      case 'streamAborted':
        if (open) {
          Object.assign(open, {
            state: 'aborted',
            tokensOut: e.tokensOut,
            generationMs: e.generationMs,
            tokensPerSecond: e.tokensPerSecond,
          });
          open = null;
        }
        break;
      case 'escalationStarted':
        rows.push({
          kind: 'marker',
          seq,
          icon: 'trending_up',
          text: escalationBanner(e.itemCount, e.configName, e.step),
        });
        break;
      case 'runStarted':
        rows.push({ kind: 'marker', seq, icon: 'play_circle', text: 'Run started' });
        break;
      case 'runEnded':
        rows.push({ kind: 'marker', seq, icon: 'stop_circle', text: 'Run ended' });
        break;
    }
  });

  return trimToLastTurns(rows, maxTurns);
}

function trimToLastTurns(rows: LlmStreamRow[], maxTurns: number): LlmStreamRow[] {
  let turns = 0;
  for (let i = rows.length - 1; i >= 0; i--) {
    if (rows[i]!.kind === 'turn' && ++turns > maxTurns) return rows.slice(i + 1);
  }
  return rows;
}
