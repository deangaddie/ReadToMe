import type { ProjectSummary, SeriesDto, SeriesPosterDto } from '@app/api';

/**
 * The Projects page's Series view as data (#207, variant D): each series joined to its projects,
 * series cards merged among the books that aren't in a series, and where the open series' panel
 * goes. The host sends `GET /api/series` and `GET /api/projects` apart; there is no combined read.
 */

/** A member book with its Series position. */
export interface SeriesMember {
  project: ProjectSummary;
  position: number;
}

/** A series with its member projects in position order. */
export interface SeriesView {
  id: string;
  name: string;
  members: SeriesMember[];
  /** The uploaded poster, the lowest book's cover, or None (the placeholder). */
  poster: SeriesPosterDto;
}

export type ShelfItem =
  | { kind: 'series'; key: string; name: string; series: SeriesView }
  | { kind: 'book'; key: string; name: string; project: ProjectSummary };

/** A book's place in its series, for the card's "Sherlock Holmes · Book 2" line and menu. */
export interface Membership {
  seriesId: string;
  name: string;
  position: number;
}

const byName = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: 'base' });

/**
 * Joins each series to the projects list. A book whose folder isn't in the list (gone outside the
 * app, or the two reads raced) is left out; the host drops it from the registry on its next read.
 */
export function seriesViews(series: SeriesDto[], projects: ProjectSummary[]): SeriesView[] {
  const byFolder = new Map(projects.map((p) => [p.folderName, p]));
  return series.map((s) => ({
    id: s.id,
    name: s.name,
    poster: s.poster,
    members: s.books
      .flatMap((b) => {
        const project = byFolder.get(b.folder);
        return project ? [{ project, position: b.position }] : [];
      })
      .sort((a, b) => a.position - b.position),
  }));
}

/** Folder → its series and position. */
export function membershipOf(views: SeriesView[]): Map<string, Membership> {
  const map = new Map<string, Membership>();
  for (const s of views)
    for (const m of s.members)
      map.set(m.project.folderName, { seriesId: s.id, name: s.name, position: m.position });
  return map;
}

/** Series cards and the books in no series, one grid sorted by name, ignoring case. */
export function shelfItems(views: SeriesView[], projects: ProjectSummary[]): ShelfItem[] {
  const members = membershipOf(views);
  const items: ShelfItem[] = [
    ...views.map((s) => ({
      kind: 'series' as const,
      key: `series:${s.id}`,
      name: s.name,
      series: s,
    })),
    ...projects
      .filter((p) => !members.has(p.folderName))
      .map((p) => ({
        kind: 'book' as const,
        key: `book:${p.folderName}`,
        name: p.title,
        project: p,
      })),
  ];
  return items.sort((a, b) => byName(a.name, b.name));
}

/**
 * Index of the card the open series' panel follows: the last card in the open card's row, so the
 * sort order never breaks. -1 when nothing is open.
 */
export function panelAfter(count: number, openIndex: number, columns: number): number {
  if (openIndex < 0) return -1;
  const cols = Math.max(1, columns);
  return Math.min(count - 1, Math.ceil((openIndex + 1) / cols) * cols - 1);
}

/** "1.5", never "1.50". */
export const formatPosition = (n: number): string => String(Number(n.toFixed(2)));
export const bookLabel = (n: number): string => `Book ${formatPosition(n)}`;

/** "4 books · Books 1–5", "1 book · Book 1", or "No books yet". */
export function seriesSummary(s: SeriesView): string {
  const first = s.members[0];
  const last = s.members.at(-1);
  if (!first || !last) return 'No books yet';
  const count = `${s.members.length} ${s.members.length === 1 ? 'book' : 'books'}`;
  const range =
    first === last
      ? bookLabel(first.position)
      : `Books ${formatPosition(first.position)}–${formatPosition(last.position)}`;
  return `${count} · ${range}`;
}

/** The number a joining book gets (Series lifecycle 5): the next whole number after the highest. */
export function nextPosition(s: SeriesView): number {
  const last = s.members.at(-1);
  return last ? Math.floor(last.position) + 1 : 1;
}
