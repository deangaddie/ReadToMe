/**
 * PROTOTYPE — throwaway (wayfinder ticket "Projects page: Books and Series views", #207).
 * Fake Projects page data with series: no API, no hub. Books and series live in memory so the
 * three Series-view variants can be judged against the real shell. Lifecycle rules follow
 * "Series lifecycle" (#206): typed Series positions (0+, two decimals, no ties, gaps fine), join
 * at the next whole number, no renumbering on leave, unique names, empty series allowed.
 */
import type { ProjectSummary } from '@app/api';
import { computed, signal } from '@app/core/signals';

export type Variant = 'A' | 'B' | 'C' | 'D';
export type View = 'books' | 'series';
export type Scenario = 'mixed' | 'none' | 'many';

export const VARIANTS: Record<Variant, string> = {
  A: 'Series cards that expand in place',
  B: 'Series cards that open a series page',
  C: 'Shelves: one row per series',
  D: 'A, with series inline among the books',
};

export const SCENARIOS: Record<Scenario, string> = {
  mixed: 'Two series, an empty one, loose books',
  none: 'No series yet',
  many: 'Many series, few loose books',
};

export interface Member {
  folder: string;
  position: number;
}

export interface Series {
  id: string;
  name: string;
  /** Uploaded poster as a CSS background; null falls back to the lowest-positioned book's cover. */
  poster: string | null;
  members: Member[];
}

const book = (
  folderName: string,
  title: string,
  author: string,
  done: number,
  total: number,
): ProjectSummary => ({
  folderName,
  title,
  author,
  audioItemDone: done,
  audioItemTotal: total,
  audioPercent: total ? Math.floor((100 * done) / total) : 0,
  coverImage: null,
  fileType: 'Epub',
});

// Project Gutenberg books only (repo rule).
const BOOKS: ProjectSummary[] = [
  book('study-in-scarlet', 'A Study in Scarlet', 'Arthur Conan Doyle', 1840, 1840),
  book('sign-of-the-four', 'The Sign of the Four', 'Arthur Conan Doyle', 900, 1702),
  book('adventures-sherlock', 'The Adventures of Sherlock Holmes', 'Arthur Conan Doyle', 0, 3410),
  book('hound-baskervilles', 'The Hound of the Baskervilles', 'Arthur Conan Doyle', 0, 0),
  book('wizard-of-oz', 'The Wonderful Wizard of Oz', 'L. Frank Baum', 1210, 1210),
  book('land-of-oz', 'The Marvelous Land of Oz', 'L. Frank Baum', 300, 1388),
  book('little-wizard-stories', 'Little Wizard Stories of Oz', 'L. Frank Baum', 0, 0),
  book('ozma-of-oz', 'Ozma of Oz', 'L. Frank Baum', 0, 0),
  book('pride-and-prejudice', 'Pride and Prejudice', 'Jane Austen', 2900, 4120),
  book('frankenstein', 'Frankenstein', 'Mary Shelley', 0, 2210),
  book('moby-dick', 'Moby-Dick', 'Herman Melville', 0, 0),
  book('tarzan-apes', 'Tarzan of the Apes', 'Edgar Rice Burroughs', 0, 0),
  book('return-tarzan', 'The Return of Tarzan', 'Edgar Rice Burroughs', 0, 0),
  book('time-machine', 'The Time Machine', 'H. G. Wells', 0, 0),
];

function seed(s: Scenario): Series[] {
  if (s === 'none') return [];
  const holmes: Series = {
    id: 's-holmes',
    name: 'Sherlock Holmes',
    poster: null,
    members: [
      { folder: 'study-in-scarlet', position: 1 },
      { folder: 'sign-of-the-four', position: 2 },
      { folder: 'adventures-sherlock', position: 3 },
      { folder: 'hound-baskervilles', position: 5 },
    ],
  };
  const oz: Series = {
    id: 's-oz',
    name: 'Oz',
    poster: 'linear-gradient(160deg, #1f7a3a, #0c3b1d 60%, #c9a227)',
    members: [
      { folder: 'wizard-of-oz', position: 1 },
      { folder: 'land-of-oz', position: 2 },
      { folder: 'little-wizard-stories', position: 2.5 },
      { folder: 'ozma-of-oz', position: 3 },
    ],
  };
  const tarzanEmpty: Series = { id: 's-tarzan', name: 'Tarzan', poster: null, members: [] };
  if (s === 'mixed') return [holmes, oz, tarzanEmpty];
  return [
    holmes,
    oz,
    {
      id: 's-tarzan',
      name: 'Tarzan',
      poster: null,
      members: [
        { folder: 'tarzan-apes', position: 1 },
        { folder: 'return-tarzan', position: 2 },
      ],
    },
    {
      id: 's-austen',
      name: 'Austen novels',
      poster: null,
      members: [{ folder: 'pride-and-prejudice', position: 2 }],
    },
  ];
}

// --- URL-backed variant and per-browser view --------------------------------------------------

const params = () => new URLSearchParams(location.search);
const initialVariant = (params().get('variant') ?? 'A').toUpperCase();
export const variant = signal<Variant>(
  (Object.keys(VARIANTS) as Variant[]).includes(initialVariant as Variant)
    ? (initialVariant as Variant)
    : 'A',
);
export function setVariant(v: Variant): void {
  variant.set(v);
  const p = params();
  p.set('variant', v);
  history.replaceState(history.state, '', `${location.pathname}?${p}`);
  openSeries.set(null);
}

const VIEW_KEY = 'r2m-proto-projects-view';
export const view = signal<View>((localStorage.getItem(VIEW_KEY) as View | null) ?? 'books');
export function setView(v: View): void {
  view.set(v);
  localStorage.setItem(VIEW_KEY, v);
}

export const scenario = signal<Scenario>('mixed');
export const books = signal<ProjectSummary[]>(BOOKS);
export const series = signal<Series[]>(seed('mixed'));
export function setScenario(s: Scenario): void {
  scenario.set(s);
  series.set(seed(s));
  openSeries.set(null);
  log(`Scenario: ${SCENARIOS[s]}`);
}

/** Variant A: the expanded card. Variant B: the open series page. Variant C: the shelf being edited. */
export const openSeries = signal<string | null>(null);

/** Last few actions, shown in the switcher so every change is visible. */
export const actions = signal<string[]>([]);
function log(line: string): void {
  actions.update((a) => [line, ...a].slice(0, 6));
}

// --- Reads ----------------------------------------------------------------------------------

const byTitle = (a: ProjectSummary, b: ProjectSummary) =>
  a.title.localeCompare(b.title, undefined, { sensitivity: 'base' });

export const sortedBooks = computed(() => [...books()].sort(byTitle));
export const sortedSeries = computed(() =>
  [...series()].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
);
export const looseBooks = computed(() =>
  sortedBooks().filter(
    (b) => !series().some((s) => s.members.some((m) => m.folder === b.folderName)),
  ),
);

export function bookOf(folder: string): ProjectSummary {
  return books().find((b) => b.folderName === folder)!;
}
export function seriesById(id: string): Series | undefined {
  return series().find((s) => s.id === id);
}
export function seriesOf(folder: string): { series: Series; position: number } | null {
  for (const s of series()) {
    const m = s.members.find((x) => x.folder === folder);
    if (m) return { series: s, position: m.position };
  }
  return null;
}
export function membersInOrder(s: Series): { book: ProjectSummary; position: number }[] {
  return [...s.members]
    .sort((a, b) => a.position - b.position)
    .map((m) => ({ book: bookOf(m.folder), position: m.position }));
}
/** "1.5", never "1.50". */
export const formatPosition = (n: number) => String(Number(n.toFixed(2)));
export const bookLabel = (n: number) => `Book ${formatPosition(n)}`;

/** Poster, else the lowest-positioned book's cover, else null (placeholder with the name). */
export function posterOf(
  s: Series,
): { kind: 'poster'; css: string } | { kind: 'cover'; book: ProjectSummary } | null {
  if (s.poster) return { kind: 'poster', css: s.poster };
  const first = membersInOrder(s)[0];
  return first ? { kind: 'cover', book: first.book } : null;
}

// --- Writes (rules from Series lifecycle) ---------------------------------------------------

const nextWhole = (s: Series) =>
  s.members.length ? Math.floor(Math.max(...s.members.map((m) => m.position))) + 1 : 1;

export function nameProblem(name: string, exceptId?: string): string | null {
  const n = name.trim();
  if (!n) return 'A series needs a name.';
  if (series().some((s) => s.id !== exceptId && s.name.toLowerCase() === n.toLowerCase()))
    return `There is already a series called "${n}".`;
  return null;
}

let nextId = 1;
export function createSeries(name: string, withFolder?: string): Series {
  const s: Series = { id: `s-new-${nextId++}`, name: name.trim(), poster: null, members: [] };
  if (withFolder) s.members.push({ folder: withFolder, position: 1 });
  series.update((all) => [...all, s]);
  log(
    `Created series "${s.name}"${withFolder ? ` with ${bookOf(withFolder).title} as Book 1` : ''}`,
  );
  return s;
}

export function renameSeries(id: string, name: string): void {
  series.update((all) => all.map((s) => (s.id === id ? { ...s, name: name.trim() } : s)));
  log(`Renamed series to "${name.trim()}"`);
}

/** Join (or Move to, after the caller's Leave confirm): the next whole number after the highest. */
export function join(id: string, folder: string): number {
  const from = seriesOf(folder);
  let position = 0;
  series.update((all) =>
    all.map((s) => {
      if (from && s.id === from.series.id && s.id !== id)
        return { ...s, members: s.members.filter((m) => m.folder !== folder) };
      if (s.id === id) {
        position = nextWhole(s);
        return { ...s, members: [...s.members, { folder, position }] };
      }
      return s;
    }),
  );
  log(
    `${bookOf(folder).title} ${from ? `moved from "${from.series.name}" ` : ''}joined "${seriesById(id)!.name}" as ${bookLabel(position)}`,
  );
  return position;
}

export function leave(folder: string): void {
  const from = seriesOf(folder);
  if (!from) return;
  series.update((all) =>
    all.map((s) =>
      s.id === from.series.id ? { ...s, members: s.members.filter((m) => m.folder !== folder) } : s,
    ),
  );
  log(`${bookOf(folder).title} left "${from.series.name}"; the others keep their numbers`);
}

/** Checks a whole set of typed positions; null when it can be saved. */
export function positionsProblem(entries: { folder: string; text: string }[]): string | null {
  const seen = new Map<number, string>();
  for (const e of entries) {
    const t = e.text.trim();
    const title = bookOf(e.folder).title;
    if (!/^\d+(\.\d{1,2})?$/.test(t))
      return `${title}: a position is a number of 0 or more with at most two decimal places.`;
    const n = Number(t);
    const other = seen.get(n);
    if (other !== undefined) return `${title} and ${other} are both ${bookLabel(n)}.`;
    seen.set(n, title);
  }
  return null;
}

export function savePositions(id: string, entries: { folder: string; text: string }[]): void {
  series.update((all) =>
    all.map((s) =>
      s.id === id
        ? {
            ...s,
            members: entries.map((e) => ({ folder: e.folder, position: Number(e.text.trim()) })),
          }
        : s,
    ),
  );
  log(`Saved positions for "${seriesById(id)!.name}"`);
}

const POSTERS = [
  'linear-gradient(160deg, #6b2d2d, #1d0f0f 70%)',
  'linear-gradient(160deg, #2d4f6b, #0f1a24 70%)',
  'linear-gradient(160deg, #5a4a1c, #1c170a 70%)',
];
/** Stands in for the upload: picking the file is the confirm, so a replace asks nothing. */
export function uploadPoster(id: string): void {
  series.update((all) =>
    all.map((s) =>
      s.id === id
        ? { ...s, poster: POSTERS[(POSTERS.indexOf(s.poster ?? '') + 1) % POSTERS.length]! }
        : s,
    ),
  );
  log(`Uploaded a poster for "${seriesById(id)!.name}"`);
}
export function clearPoster(id: string): void {
  series.update((all) => all.map((s) => (s.id === id ? { ...s, poster: null } : s)));
  log(`Cleared the poster of "${seriesById(id)!.name}"; it falls back to the lowest book's cover`);
}

export function deleteSeries(id: string): void {
  const s = seriesById(id)!;
  series.update((all) => all.filter((x) => x.id !== id));
  if (openSeries() === id) openSeries.set(null);
  log(`Deleted series "${s.name}"; its ${s.members.length} books stay as they are`);
}

export function deleteBook(folder: string): void {
  const from = seriesOf(folder);
  const title = bookOf(folder).title;
  if (from) leave(folder);
  books.update((all) => all.filter((b) => b.folderName !== folder));
  log(`Deleted project ${title}`);
}
