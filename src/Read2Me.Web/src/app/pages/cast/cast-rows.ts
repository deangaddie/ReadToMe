import { CharacterSummaryDto, NarratorDto } from '@app/api';
import { StatusKind } from '@app/ui/status-chip/status-chip';

export const CAST_SORTS = ['name', 'lines', 'readiness'] as const;
export type CastSort = (typeof CAST_SORTS)[number];

export const CAST_SORT_LABELS: Record<CastSort, string> = {
  name: 'Name',
  lines: 'Lines',
  readiness: 'Voice readiness',
};

export interface ReadinessChip {
  status: StatusKind;
  label: string;
  tooltip: string;
}

/**
 * The `ready/total` voices chip (research §4 list row): nothing when the character has no voices;
 * error when none is ready, warn when some are, info when all are. The label collapses to the
 * total when every voice is ready.
 */
export function readinessChip(ready: number, total: number): ReadinessChip | null {
  if (total <= 0) return null;
  const status: StatusKind = ready === 0 ? 'error' : ready < total ? 'warn' : 'info';
  const label = ready === total ? `${total}` : `${ready} / ${total}`;
  const tooltip = `${ready} of ${total} ${total === 1 ? 'voice' : 'voices'} ready for TTS`;
  return { status, label, tooltip };
}

/** The seed Narrator row reads "Narrator → Watson" while the book is narrated by Watson. */
export function displayName(row: CharacterSummaryDto, narrator: NarratorDto | null): string {
  if (row.isNarrator && narrator?.isLinked) return `Narrator → ${narrator.displayName}`;
  return row.name;
}

/** Whether the row shows the book icon: the seed Narrator or the character narrating the book. */
export function hasBookIcon(row: CharacterSummaryDto): boolean {
  return row.isNarrator || row.narratesBook;
}

/** Name or alias contains the query, case-insensitively; an empty query keeps everything. */
export function filterRows(
  rows: readonly CharacterSummaryDto[],
  query: string,
): CharacterSummaryDto[] {
  const q = query.trim().toLowerCase();
  if (!q) return [...rows];
  return rows.filter(
    (r) =>
      r.name.toLowerCase().includes(q) || r.aliases.some((a) => a.name.toLowerCase().includes(q)),
  );
}

/**
 * Sorts with the seed Narrator pinned first whatever the key. `lines` and `readiness` sort
 * descending (the busiest / most complete first) and break ties by name.
 */
export function sortRows(
  rows: readonly CharacterSummaryDto[],
  sort: CastSort,
): CharacterSummaryDto[] {
  const byName = (a: CharacterSummaryDto, b: CharacterSummaryDto) =>
    a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
  const compare = (a: CharacterSummaryDto, b: CharacterSummaryDto): number => {
    if (a.isNarrator !== b.isNarrator) return a.isNarrator ? -1 : 1;
    switch (sort) {
      case 'lines':
        return b.lineCount - a.lineCount || byName(a, b);
      case 'readiness':
        return readiness(b) - readiness(a) || b.voiceCount - a.voiceCount || byName(a, b);
      default:
        return byName(a, b);
    }
  };
  return [...rows].sort(compare);
}

/** Fraction of voices ready; a character with no voices sorts below one with an unready voice. */
function readiness(row: CharacterSummaryDto): number {
  return row.voiceCount === 0 ? -1 : row.readyVoiceCount / row.voiceCount;
}
