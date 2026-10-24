import type {
  CharacterSummaryDto,
  CharacterVoicesDto,
  Guid,
  PronunciationMatchInBook,
  TryVoice,
} from '@app/api';
import type { QueueMessage, VoiceBatchState } from '@app/live/live-messages';
import { isAttributionActive, isAudioActive } from '@app/activity/activity-jobs';

/**
 * Try it's rules as pure functions (ticket 13, variant F): which matches the sample cards show,
 * Swap, Other samples and the All N matches ticks, the voice picker, and why playing is off. The
 * matches are the impact query's, in book order; the host owns matching and the sentence cut.
 */

/** How many sample cards Try it shows at most. */
export const SAMPLE_CARDS = 3;

/** A match, as a card names it: stable while the draft's spoken form changes (its sentence doesn't). */
export function matchKey(m: PronunciationMatchInBook): string {
  return `${m.itemId}:${m.sentenceStart}:${m.matchStart}`;
}

/** One sample card: the match it shows and the voice it plays in, kept across Swap. */
export interface SampleCard {
  key: string;
  voice: TryVoice;
}

/**
 * Up to {@link SAMPLE_CARDS} matches spread over the book: one near the first, the middle and the
 * last match. Each comes from its own third of the matches, nearest its anchor, preferring a
 * speaker no earlier pick has, so the word is heard in varied company.
 */
export function pickSamples(matches: readonly PronunciationMatchInBook[]): string[] {
  const n = matches.length;
  if (n <= SAMPLE_CARDS) return matches.map(matchKey);
  const thirds: [number, number, number][] = [
    [0, 0, Math.floor(n / 3)],
    [Math.floor((n - 1) / 2), Math.floor(n / 3), Math.floor((2 * n) / 3)],
    [n - 1, Math.floor((2 * n) / 3), n],
  ];
  const speakers = new Set<string | null>();
  return thirds.map(([anchor, from, to]) => {
    const byNearness = range(from, to).sort((a, b) => Math.abs(a - anchor) - Math.abs(b - anchor));
    const pick = byNearness.find((i) => !speakers.has(matches[i]!.speaker)) ?? anchor;
    speakers.add(matches[pick]!.speaker);
    return matchKey(matches[pick]!);
  });
}

/**
 * The cards for the matches: kept where their match still stands (a spoken-form edit changes
 * nothing about where the word is), picked afresh when the matches no longer hold them all.
 */
export function cardsFor(
  matches: readonly PronunciationMatchInBook[],
  cards: readonly SampleCard[],
  voice: TryVoice,
): SampleCard[] {
  const keys = new Set(matches.map(matchKey));
  if (cards.length > 0 && cards.every((c) => keys.has(c.key))) return [...cards];
  return pickSamples(matches).map((key, i) => ({
    key,
    voice: cards[i]?.voice ?? voice,
  }));
}

/** Card `index` showing the next match, in book order and wrapping, no card shows; its voice kept. */
export function swap(
  matches: readonly PronunciationMatchInBook[],
  cards: readonly SampleCard[],
  index: number,
): SampleCard[] {
  const card = cards[index];
  if (!card) return [...cards];
  const keys = matches.map(matchKey);
  const shown = new Set(cards.map((c) => c.key));
  const at = keys.indexOf(card.key);
  for (let step = 1; step < keys.length; step++) {
    const next = keys[(at + step + keys.length) % keys.length]!;
    if (!shown.has(next)) return cards.map((c, i) => (i === index ? { ...c, key: next } : c));
  }
  return [...cards];
}

/**
 * Other samples: as many cards as now, on matches drawn at random, avoiding those shown when there
 * are enough others and preferring speakers not yet drawn. Each card keeps its voice.
 */
export function reshuffle(
  matches: readonly PronunciationMatchInBook[],
  cards: readonly SampleCard[],
  random: () => number = Math.random,
): SampleCard[] {
  const count = Math.min(Math.max(cards.length, SAMPLE_CARDS), matches.length);
  const shown = new Set(cards.map((c) => c.key));
  const fresh = matches.filter((m) => !shown.has(matchKey(m)));
  const pool = shuffle(fresh.length >= count ? fresh : [...matches], random);
  const speakers = new Set<string | null>();
  const picked: PronunciationMatchInBook[] = [];
  for (const m of pool) {
    if (picked.length === count) break;
    if (!speakers.has(m.speaker)) {
      picked.push(m);
      speakers.add(m.speaker);
    }
  }
  for (const m of pool) {
    if (picked.length === count) break;
    if (!picked.includes(m)) picked.push(m);
  }
  const voices = cards.map((c) => c.voice);
  return picked.map((m, i) => ({
    key: matchKey(m),
    voice: voices[i] ?? voices[0] ?? 'item',
  }));
}

/**
 * A tick in All N matches: an unticked match becomes the newest card, the oldest dropping past
 * {@link SAMPLE_CARDS}; a ticked one stops being a card.
 */
export function toggleCard(
  cards: readonly SampleCard[],
  key: string,
  voice: TryVoice,
): SampleCard[] {
  if (cards.some((c) => c.key === key)) return cards.filter((c) => c.key !== key);
  return [...cards, { key, voice }].slice(-SAMPLE_CARDS);
}

// ---- the voice picker -----------------------------------------------------------------------------

export interface VoiceOption {
  id: Guid;
  name: string;
}

export interface VoiceGroup {
  character: string;
  voices: VoiceOption[];
}

export interface VoicePicker {
  /** By character, the narrator's first; empty when the project has no voices (the picker hides). */
  groups: VoiceGroup[];
  /** The narrator's default voice, else the first voice; the item's own when there are none. */
  defaultVoice: TryVoice;
}

/** The character narration speaks with: the one the narrator link names, else the seed Narrator. */
export function narratorOf(cast: readonly CharacterSummaryDto[]): CharacterSummaryDto | null {
  return cast.find((c) => c.narratesBook) ?? cast.find((c) => c.isNarrator) ?? null;
}

/** The picker from the cast and each character's voices; only voices with audio can speak. */
export function voicePicker(
  cast: readonly CharacterSummaryDto[],
  voices: ReadonlyMap<Guid, CharacterVoicesDto>,
): VoicePicker {
  const narrator = narratorOf(cast);
  const ordered = [
    ...(narrator ? [narrator] : []),
    ...cast
      .filter((c) => c !== narrator)
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
  ];
  const groups = ordered
    .map((c) => ({
      character: c,
      voices: (voices.get(c.id)?.voices ?? [])
        .filter((v) => !!v.audioFileName)
        .map((v) => ({ id: v.id, name: v.name })),
    }))
    .filter((g) => g.voices.length > 0)
    .map((g) => ({ character: g.character.name, voices: g.voices }));
  if (groups.length === 0) return { groups, defaultVoice: 'item' };
  const narratorVoices = narrator ? voices.get(narrator.id) : undefined;
  const narratorDefault = narratorVoices?.defaultVoiceId;
  const playable = groups.flatMap((g) => g.voices);
  const id = playable.some((v) => v.id === narratorDefault) ? narratorDefault! : playable[0]!.id;
  return { groups, defaultVoice: { voiceId: id } };
}

/** The `<select>` value for a try-out voice. */
export const ITEM_VOICE = 'item';

export function voiceValue(voice: TryVoice): string {
  return voice === 'item' ? ITEM_VOICE : voice.voiceId;
}

export function voiceFromValue(value: string): TryVoice {
  return value === ITEM_VOICE ? 'item' : { voiceId: value };
}

// ---- why playing is off ---------------------------------------------------------------------------

/**
 * Why no try-out can play, from the activity state the web holds: attribution, audio generation
 * or a voice batch queued or running anywhere has the GPU (the host's 409 says the same). Null
 * when it is free.
 */
export function gpuBusyReason(
  queue: QueueMessage | null,
  voiceBatch: VoiceBatchState,
): string | null {
  const holder =
    queue && isAttributionActive(queue.attribution)
      ? 'Attribution'
      : queue && isAudioActive(queue.audio)
        ? 'Audio generation'
        : voiceBatch.isRunning
          ? 'a voice batch'
          : null;
  return holder === null ? null : `Can't play while ${holder} is running: it has the GPU`;
}

/** Why a play button is off: viewing a step first, then the GPU. */
export function playBlockedReason(readOnly: string | null, gpu: string | null): string | null {
  return readOnly ?? gpu;
}

function range(from: number, to: number): number[] {
  return Array.from({ length: Math.max(0, to - from) }, (_, i) => from + i);
}

function shuffle<T>(items: T[], random: () => number): T[] {
  const out = [...items];
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(random() * (i + 1));
    [out[i], out[j]] = [out[j]!, out[i]!];
  }
  return out;
}

/** Why the spoken-form plays are off: there is no spoken form to say yet. */
export function spokenPlayBlockedReason(spokenForm: string | null): string | null {
  return spokenForm?.trim() ? null : 'Type a spoken form to hear it';
}

/** Why Swap and Other samples are off: every match is already on a card. */
export function otherSampleBlockedReason(matchCount: number, cardCount: number): string | null {
  return matchCount > cardCount ? null : 'Every match is already a sample';
}
