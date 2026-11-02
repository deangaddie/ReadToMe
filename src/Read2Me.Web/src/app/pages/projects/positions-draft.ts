import { bookLabel, formatPosition, type SeriesView } from './series-shelf';

/**
 * Edit book numbers (#207, 4): a draft of a series' Series positions, typed as text and checked as
 * one set, so swapping two books never hits a tie halfway (Series lifecycle 6). The host checks
 * the same rules again on `PUT /api/series/{id}/positions`.
 */
export interface DraftRow {
  folder: string;
  title: string;
  /** The saved number. */
  position: number;
  /** What the user typed. */
  text: string;
}

/** 0 or more, at most two decimal places. */
const POSITION = /^\d+(\.\d{1,2})?$/;

export function draftRows(series: SeriesView): DraftRow[] {
  return series.members.map((m) => ({
    folder: m.project.folderName,
    title: m.project.title,
    position: m.position,
    text: formatPosition(m.position),
  }));
}

/** The first problem with the set (a bad format, then a tie naming both books), or null. */
export function positionsProblem(rows: DraftRow[]): string | null {
  const seen = new Map<number, string>();
  for (const row of rows) {
    const text = row.text.trim();
    if (!POSITION.test(text))
      return `${row.title}: a book number is 0 or more, with at most two decimal places.`;
    const n = Number(text);
    const other = seen.get(n);
    if (other !== undefined) return `${other} and ${row.title} are both ${bookLabel(n)}.`;
    seen.set(n, row.title);
  }
  return null;
}

/** True when any typed number differs from the saved one. */
export function draftChanged(rows: DraftRow[]): boolean {
  return rows.some((r) => Number(r.text.trim()) !== r.position || r.text.trim() === '');
}

/** Why Save numbers is off: the set's problem, else nothing changed; null when it can save. */
export function saveBlockedReason(rows: DraftRow[]): string | null {
  return positionsProblem(rows) ?? (draftChanged(rows) ? null : 'No book number has changed.');
}

/** The body of the positions PUT: every member's number. */
export function draftPositions(rows: DraftRow[]): Record<string, number> {
  return Object.fromEntries(rows.map((r) => [r.folder, Number(r.text.trim())]));
}
