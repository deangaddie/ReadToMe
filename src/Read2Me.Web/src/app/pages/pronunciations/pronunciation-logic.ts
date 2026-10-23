import type { PronunciationOverlap, PronunciationRef } from '@app/api';
import { plural } from '@app/ui/wording';

/**
 * The Pronunciations page and editor's wording and rules, as pure functions of what the host
 * answered. The matching itself is the host's (`PronunciationMatcher`); nothing here re-implements it.
 */

/** A Pronunciation's or a draft's reach in the book. */
export interface Reach {
  matchCount: number;
  itemCount: number;
  withAudioCount: number;
}

/** "14 matches in 9 items · 3 have audio", or "No matches in the book". */
export function countLine(reach: Reach): string {
  if (reach.matchCount === 0) return 'No matches in the book';
  const audio = reach.withAudioCount === 1 ? '1 has audio' : `${reach.withAudioCount} have audio`;
  return (
    `${reach.matchCount} match${reach.matchCount === 1 ? '' : 'es'} in ` +
    `${reach.itemCount} item${plural(reach.itemCount)} · ${audio}`
  );
}

/** The advice line under Save: what saving discards, or null when it discards nothing. */
export function discardAdvice(audioDiscardCount: number): string | null {
  if (audioDiscardCount <= 0) return null;
  return `Saving discards the audio of ${audioDiscardCount} item${plural(audioDiscardCount)}; it can't be brought back.`;
}

/** One note per overlapping entry: which of the two applies where both match (longest wins). */
export function overlapNotes(overlaps: readonly PronunciationOverlap[]): string[] {
  return overlaps.map((o) =>
    o.draftWins
      ? `Wins over “${o.writtenForm}” where both match.`
      : `Inside “${o.writtenForm}”, that longer entry wins.`,
  );
}

/** The warning while another entry has the draft's written form (Save stays off), or null. */
export function duplicateWarning(duplicateOf: PronunciationRef | null): string | null {
  return duplicateOf === null ? null : `“${duplicateOf.writtenForm}” is already in the list.`;
}

/** "Saved. 12 items need generating." after a save or a delete; just "Saved." when none do. */
export function savedToast(verb: 'Saved' | 'Deleted', needsAudioCount: number): string {
  if (needsAudioCount <= 0) return `${verb}.`;
  const needs = needsAudioCount === 1 ? 'needs' : 'need';
  return `${verb}. ${needsAudioCount} item${plural(needsAudioCount)} ${needs} generating.`;
}

/** The two forms a draft edits. */
export interface Forms {
  writtenForm: string;
  spokenForm: string;
}

/** Why Save and Delete are off while audio generation is queued or running (the host's 409 wording). */
export const AUDIO_RUNNING = 'Audio generation is running: wait for it or cancel it';

/** Trimmed, inner whitespace runs as one space: how the host stores a form. */
function normalise(form: string): string {
  return form.trim().replace(/\s+/g, ' ');
}

/** A new draft is dirty once either form has text; an entry's once a form differs from it as stored. */
export function isDirty(saved: Forms | null, draft: Forms): boolean {
  const written = normalise(draft.writtenForm);
  const spoken = normalise(draft.spokenForm);
  if (saved === null) return written !== '' || spoken !== '';
  return written !== saved.writtenForm || spoken !== saved.spokenForm;
}

export interface DeleteGate {
  /** `StepView.readOnly`: the viewing reason, or null. */
  readOnly: string | null;
  /** Items queued or processing for audio in this project. */
  audioInFlight: number;
}

export interface SaveGate extends DeleteGate {
  duplicateOf: PronunciationRef | null;
  /** The host's 400 detail for the draft, or null. */
  invalid: string | null;
}

/** Why Delete is off, or null. */
export function deleteBlockedReason(gate: DeleteGate): string | null {
  if (gate.readOnly !== null) return gate.readOnly;
  return gate.audioInFlight > 0 ? AUDIO_RUNNING : null;
}

/** Why Save is off for a reason worth saying (an unchanged draft is off too, without one), or null. */
export function saveBlockedReason(gate: SaveGate): string | null {
  return deleteBlockedReason(gate) ?? duplicateWarning(gate.duplicateOf) ?? gate.invalid;
}

/** The entries whose written or spoken form holds the query, ignoring case and surrounding space. */
export function filterEntries<T extends Forms>(entries: readonly T[], query: string): T[] {
  const q = query.trim().toLowerCase();
  if (q === '') return [...entries];
  return entries.filter(
    (e) => e.writtenForm.toLowerCase().includes(q) || e.spokenForm.toLowerCase().includes(q),
  );
}
