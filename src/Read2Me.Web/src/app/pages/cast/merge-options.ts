import { CharacterSummaryDto, Guid } from '@app/api';

/**
 * The merge dialog's rules (research §4 "Merge"): the survivor is any other non-narrator
 * character, the merged character's name is offered as an alias by default, and the merged
 * character's voices go with it.
 */
export interface MergeForm {
  survivorId: Guid | null;
  addNameAsAlias: boolean;
}

/** Who a character may be merged into: every other character except the seed Narrator. */
export function mergeCandidates(
  rows: readonly CharacterSummaryDto[],
  mergedId: Guid,
): CharacterSummaryDto[] {
  return rows.filter((r) => r.id !== mergedId && !r.isNarrator);
}

/** The dialog's initial state: the first candidate preselected, alias on. */
export function initialMergeForm(candidates: readonly CharacterSummaryDto[]): MergeForm {
  return { survivorId: candidates[0]?.id ?? null, addNameAsAlias: true };
}

/** The reason the form cannot be submitted, or null when it can. */
export function validateMerge(
  form: MergeForm,
  candidates: readonly CharacterSummaryDto[],
  mergedId: Guid,
): string | null {
  if (!form.survivorId) return 'Select a character to merge into.';
  if (form.survivorId === mergedId) return 'A character cannot be merged into itself.';
  if (!candidates.some((c) => c.id === form.survivorId))
    return 'Select a character to merge into.';
  return null;
}

/**
 * The warning shown when the merged character has voices — they are deleted with it, along with
 * any generated audio and voice rules. Null when it has none.
 */
export function lostVoicesWarning(
  mergedName: string,
  voiceNames: readonly string[],
): string | null {
  if (voiceNames.length === 0) return null;
  const goes =
    voiceNames.length === 1 ? 'its voice goes with it' : `its ${voiceNames.length} voices go with it`;
  return (
    `${mergedName} is deleted by this merge, and ${goes} — including any generated audio and ` +
    `voice rules: ${[...voiceNames].sort((a, b) => a.localeCompare(b)).join(', ')}. ` +
    'The survivor keeps its own voices.'
  );
}
