import { AudioGenEvent } from '@app/live/hub-events';

export type AudioPhaseState = 'pending' | 'active' | 'ok' | 'warn' | 'error';
export type AudioPhaseId = 'generate' | 'normalize' | 'postProcess' | 'transcribe' | 'verify';

export interface AudioPhase {
  id: AudioPhaseId;
  label: string;
  state: AudioPhaseState;
  /** One line per post-process step, or the transcript / verify summary. */
  lines: string[];
}

export interface AudioCard {
  key: string;
  id: string;
  attempt: number;
  character?: string | null | undefined;
  text?: string | null | undefined;
  phases: AudioPhase[];
  failed: boolean;
  reason?: string | null | undefined;
}

const PHASES: { id: AudioPhaseId; label: string }[] = [
  { id: 'generate', label: 'Generate' },
  { id: 'normalize', label: 'Normalize' },
  { id: 'postProcess', label: 'Post-process' },
  { id: 'transcribe', label: 'Transcribe' },
  { id: 'verify', label: 'Verify' },
];

function newCard(
  id: string,
  attempt: number,
  character?: string | null,
  text?: string | null,
): AudioCard {
  const phases = PHASES.map<AudioPhase>((p) => ({ ...p, state: 'pending', lines: [] }));
  phases[0]!.state = 'active';
  return { key: `${id}#${attempt}`, id, attempt, character, text, phases, failed: false };
}

function phase(card: AudioCard, id: AudioPhaseId): AudioPhase {
  return card.phases.find((p) => p.id === id)!;
}

/** Marks `id` finished with `state` and moves the `active` marker to the next pending phase. */
function advance(card: AudioCard, id: AudioPhaseId, state: AudioPhaseState): void {
  const idx = card.phases.findIndex((p) => p.id === id);
  card.phases[idx]!.state = state;
  for (let i = 0; i < idx; i++) {
    if (card.phases[i]!.state === 'pending' || card.phases[i]!.state === 'active')
      card.phases[i]!.state = 'ok';
  }
  const next = card.phases[idx + 1];
  if (next && next.state === 'pending') next.state = 'active';
}

/**
 * Folds audio-generation events into one card per item attempt with five phase steps
 * (research/live-events.md §1.3). Keeps the last `maxCards` cards.
 */
export function foldAudioCards(events: readonly AudioGenEvent[], maxCards = 50): AudioCard[] {
  const cards = new Map<string, AudioCard>();
  const get = (id: string, attempt: number) => cards.get(`${id}#${attempt}`);

  for (const e of events) {
    if (e.kind === 'itemStarted') {
      const card = newCard(e.id, e.attempt, e.character, e.text);
      cards.delete(card.key); // re-insert so a restarted attempt moves to the end
      cards.set(card.key, card);
      continue;
    }
    const card = get(e.id, e.attempt);
    if (!card) continue;
    switch (e.kind) {
      case 'audioGenerated':
        advance(card, 'generate', 'ok');
        break;
      case 'normalized':
        advance(card, 'normalize', e.ok ? 'ok' : 'warn');
        if (!e.ok && e.reason) phase(card, 'normalize').lines.push(e.reason);
        break;
      case 'postProcessed': {
        const p = phase(card, 'postProcess');
        p.lines.push(
          `${e.stepId}: ${e.applied ? 'applied' : 'skipped'}${e.reason ? ` — ${e.reason}` : ''}`,
        );
        // A skipped step is informational (its reason is in the line), never a warning.
        advance(card, 'postProcess', 'ok');
        break;
      }
      case 'transcribed':
        phase(card, 'transcribe').lines.push(e.transcript);
        advance(card, 'transcribe', 'ok');
        break;
      case 'verified': {
        const p = phase(card, 'verify');
        const bits: string[] = [];
        if (e.wer != null) bits.push(`WER ${(e.wer * 100).toFixed(1)}%`);
        if (e.rescued) bits.push('rescued');
        if (e.reason) bits.push(e.reason);
        if (bits.length) p.lines.push(bits.join(' · '));
        advance(card, 'verify', e.ok ? (e.rescued ? 'warn' : 'ok') : 'error');
        break;
      }
      case 'failed': {
        card.failed = true;
        card.reason = e.reason;
        const active = card.phases.find((p) => p.state === 'active');
        if (active) active.state = 'error';
        break;
      }
    }
  }

  const all = Array.from(cards.values());
  return all.length > maxCards ? all.slice(all.length - maxCards) : all;
}
