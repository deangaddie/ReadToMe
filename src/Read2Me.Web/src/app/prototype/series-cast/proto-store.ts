/**
 * PROTOTYPE — throwaway (wayfinder ticket "Character linking: link, suggest, unlink, edit a series
 * Voice", #208). Fake Cast pages for three books of one series: no API, no hub. Rules from Series
 * storage (#205): Voices replicated under one Voice Id in every linked book, edits written to
 * every linked book, "differs from series" resolved by push or take, a shared Voice's delete
 * refused while another book's positional rule targets it, one Character per book per series
 * character, linking merges both sides' Voices after a confirm.
 */
import { computed, signal } from '@app/core/signals';

export type Variant = 'A' | 'B' | 'C';
export const VARIANTS: Record<Variant, string> = {
  A: 'Series section in the character detail',
  B: 'Series cast board on the Projects panel',
  C: 'Suggestions review on the Cast page',
};

export type Scenario = 'joined' | 'linked' | 'differs';
export const SCENARIOS: Record<Scenario, string> = {
  joined: 'Book 2 just joined: nothing linked there yet',
  linked: 'Main characters linked in all three books',
  differs: 'Watson differs from series in Book 2',
};

export interface Voice {
  id: string;
  name: string;
  description: string;
  /** Stands in for the reference audio's content hash. */
  audio: string;
}

export interface Rule {
  kind: 'default' | 'positional';
  voiceId: string;
  /** Positional rules only: where they apply. */
  where?: string;
}

export interface Character {
  id: string;
  name: string;
  aliases: string[];
  lines: number;
  voices: Voice[];
  rules: Rule[];
  /** Series character id, when linked. */
  link: string | null;
}

export interface Book {
  folder: string;
  title: string;
  position: number;
  characters: Character[];
}

export interface SeriesCharacter {
  id: string;
  label: string;
}

const v = (id: string, name: string, description: string, audio = `${id}-a`): Voice => ({
  id,
  name,
  description,
  audio,
});

function holmesBooks(s: Scenario): { books: Book[]; series: SeriesCharacter[] } {
  const holmesV = [v('v-holmes-1', 'Clipped baritone', 'Precise, quick, faintly amused')];
  const watsonV = [
    v('v-watson-1', 'Warm narrator', 'Steady, kindly, a little weary'),
    v('v-watson-2', 'Watson, alarmed', 'Breathless, higher, faster'),
  ];
  const narratorV = [v('v-narr', 'Even narrator', 'Unhurried, Victorian, faintly dry')];
  const lestradeV = [v('v-lestrade-1', 'Sharp Londoner', 'Terse, ferrety, defensive')];
  const linked = s !== 'joined';
  const ch = (
    id: string,
    name: string,
    aliases: string[],
    lines: number,
    voices: Voice[],
    link: string | null,
    rules?: Rule[],
  ): Character => ({
    id,
    name,
    aliases,
    lines,
    voices: voices.map((x) => ({ ...x })),
    rules: rules ?? (voices[0] ? [{ kind: 'default', voiceId: voices[0].id }] : []),
    link,
  });
  const book1: Book = {
    folder: 'study-in-scarlet',
    title: 'A Study in Scarlet',
    position: 1,
    characters: [
      ch('c1-narr', 'Narrator', [], 812, narratorV, 'sc-narrator'),
      ch('c1-holmes', 'Sherlock Holmes', ['Holmes', 'Mr. Holmes'], 402, holmesV, 'sc-holmes'),
      ch(
        'c1-watson',
        'John Watson',
        ['Watson', 'Dr. Watson', 'Doctor'],
        288,
        watsonV,
        'sc-watson',
        [
          { kind: 'default', voiceId: 'v-watson-1' },
          { kind: 'positional', voiceId: 'v-watson-2', where: 'Part 1, chapter 7' },
        ],
      ),
      ch('c1-lestrade', 'Lestrade', ['Inspector Lestrade'], 61, lestradeV, 'sc-lestrade'),
      ch(
        'c1-hope',
        'Jefferson Hope',
        ['Hope'],
        140,
        [v('v-hope', 'Frontier drawl', 'Low, hard, Utah')],
        null,
      ),
    ],
  };
  const book2: Book = {
    folder: 'sign-of-the-four',
    title: 'The Sign of the Four',
    position: 2,
    characters: [
      linked
        ? ch('c2-narr', 'Narrator', [], 760, narratorV, 'sc-narrator')
        : ch(
            'c2-narr',
            'Narrator',
            [],
            760,
            [v('v-n2', 'Default narrator', 'Neutral, made at import')],
            null,
          ),
      linked
        ? ch('c2-holmes', 'Sherlock Holmes', ['Holmes'], 377, holmesV, 'sc-holmes')
        : ch(
            'c2-holmes',
            'Holmes',
            ['Sherlock Holmes', 'Mr. Holmes'],
            377,
            [v('v-h2', 'Young Holmes', 'Lighter, eager')],
            null,
          ),
      linked
        ? ch(
            'c2-watson',
            'Dr. Watson',
            ['Watson', 'John'],
            301,
            s === 'differs'
              ? [
                  v(
                    'v-watson-1',
                    'Warm narrator',
                    'Steady, kindly, a little weary',
                    'v-watson-1-OLD',
                  ),
                  watsonV[1]!,
                ]
              : watsonV,
            'sc-watson',
          )
        : ch('c2-watson', 'Dr. Watson', ['Watson', 'John'], 301, [], null),
      linked
        ? ch(
            'c2-athelney',
            'Athelney Jones',
            ['Jones', 'Inspector Jones'],
            74,
            [v('v-jones', 'Bluff detective', 'Loud, pompous')],
            null,
          )
        : ch(
            'c2-athelney',
            'Athelney Jones',
            ['Jones', 'Inspector Jones'],
            74,
            [v('v-jones', 'Bluff detective', 'Loud, pompous')],
            null,
          ),
      ch(
        'c2-morstan',
        'Mary Morstan',
        ['Miss Morstan'],
        96,
        [v('v-mary', 'Gentle governess', 'Soft, composed')],
        null,
      ),
      ch(
        'c2-small',
        'Jonathan Small',
        ['Small'],
        155,
        [v('v-small', 'Convict', 'Rough, rueful')],
        null,
      ),
    ],
  };
  const book3: Book = {
    folder: 'adventures-sherlock',
    title: 'The Adventures of Sherlock Holmes',
    position: 3,
    characters: [
      ch('c3-narr', 'Narrator', [], 1500, narratorV, 'sc-narrator'),
      ch('c3-holmes', 'Holmes', ['Sherlock Holmes'], 920, holmesV, 'sc-holmes'),
      ch('c3-watson', 'Watson', ['Dr. Watson', 'Doctor'], 610, watsonV, 'sc-watson'),
      ch('c3-lestrade', 'Inspector Lestrade', ['Lestrade'], 40, lestradeV, 'sc-lestrade'),
      ch(
        'c3-irene',
        'Irene Adler',
        ['The woman'],
        22,
        [v('v-irene', 'Contralto', 'Cool, amused')],
        null,
      ),
    ],
  };
  return {
    books: [book1, book2, book3],
    series: [
      { id: 'sc-narrator', label: 'Narrator' },
      { id: 'sc-holmes', label: 'Sherlock Holmes' },
      { id: 'sc-watson', label: 'John Watson' },
      { id: 'sc-lestrade', label: 'Lestrade' },
    ],
  };
}

// --- State -------------------------------------------------------------------------------------

const params = () => new URLSearchParams(location.search);
const initialVariant = (params().get('variant') ?? 'A').toUpperCase() as Variant;
export const variant = signal<Variant>(initialVariant in VARIANTS ? initialVariant : 'A');
export function setVariant(x: Variant): void {
  variant.set(x);
  const p = params();
  p.set('variant', x);
  history.replaceState(history.state, '', `${location.pathname}?${p}`);
}

export const scenario = signal<Scenario>('joined');
const seeded = holmesBooks('joined');
export const books = signal<Book[]>(seeded.books);
export const seriesChars = signal<SeriesCharacter[]>(seeded.series);
/** "book|characterId" pairs the user said are not the same person. */
export const dismissed = signal<Set<string>>(new Set());
export const currentBook = signal('sign-of-the-four');
export const selected = signal<string>('c2-holmes');

export function setScenario(s: Scenario): void {
  const seed = holmesBooks(s);
  scenario.set(s);
  books.set(seed.books);
  seriesChars.set(seed.series);
  dismissed.set(new Set());
  log(`Scenario: ${SCENARIOS[s]}`);
}

export const actions = signal<string[]>([]);
export function log(line: string): void {
  actions.update((a) => [line, ...a].slice(0, 6));
}

// --- Reads -------------------------------------------------------------------------------------

export const book = computed(() => books().find((b) => b.folder === currentBook())!);
export const bookByFolder = (folder: string) => books().find((b) => b.folder === folder)!;
export const character = computed(
  () => book().characters.find((c) => c.id === selected()) ?? book().characters[0]!,
);
export const seriesChar = (id: string) => seriesChars().find((s) => s.id === id)!;

/** Every (book, character) linked to a series character, in Series position order. */
export function linkedPairs(scId: string): { book: Book; character: Character }[] {
  return books()
    .map((b) => ({ book: b, character: b.characters.find((c) => c.link === scId) }))
    .filter((x): x is { book: Book; character: Character } => !!x.character);
}

const norm = (s: string) =>
  s
    .toLowerCase()
    .replace(/^(mr|mrs|miss|dr|inspector)\.?\s+/, '')
    .trim();
const namesOf = (c: Character) => [c.name, ...c.aliases].map(norm);

/**
 * Stand-in for "Link suggestion matching" (still fog): a book's unlinked Character is suggested
 * for a series character when any of its names matches any name of a linked Character elsewhere.
 */
export function suggestionsFor(
  folder: string,
): { character: Character; sc: SeriesCharacter; via: string }[] {
  const b = bookByFolder(folder);
  const out: { character: Character; sc: SeriesCharacter; via: string }[] = [];
  for (const c of b.characters) {
    if (c.link || dismissed().has(`${folder}|${c.id}`)) continue;
    for (const sc of seriesChars()) {
      if (b.characters.some((x) => x.link === sc.id)) continue;
      const hit = linkedPairs(sc.id).find((p) =>
        namesOf(p.character).some((n) => namesOf(c).includes(n)),
      );
      if (hit) {
        out.push({ character: c, sc, via: `${hit.character.name} in ${hit.book.title}` });
        break;
      }
    }
  }
  return out;
}

/** The series' copy of a Voice: the one in the lowest-positioned other linked book. */
function seriesCopy(scId: string, voiceId: string, exceptFolder: string): Voice | undefined {
  for (const p of linkedPairs(scId)) {
    if (p.book.folder === exceptFolder) continue;
    const found = p.character.voices.find((x) => x.id === voiceId);
    if (found) return found;
  }
  return undefined;
}

export function differs(folder: string, c: Character): boolean {
  if (!c.link) return false;
  const pairs = linkedPairs(c.link);
  if (pairs.length < 2) return false;
  // The series copy is the one most linked books hold (ties: the lowest Series position).
  const counts = new Map<string, number>();
  for (const p of pairs)
    counts.set(signature(p.character.voices), (counts.get(signature(p.character.voices)) ?? 0) + 1);
  const best = pairs
    .map((p) => signature(p.character.voices))
    .reduce((a, b) => ((counts.get(b) ?? 0) > (counts.get(a) ?? 0) ? b : a));
  return signature(c.voices) !== best;
}

const signature = (vs: Voice[]) =>
  vs
    .map((x) => `${x.id}|${x.name}|${x.description}|${x.audio}`)
    .sort()
    .join(';');

/** What each side would gain if `c` links to `sc` (Voices are merged by Voice Id). */
export function mergePreview(
  folder: string,
  c: Character,
  scId: string,
): { bookGains: Voice[]; seriesGains: Voice[] } {
  const pairs = linkedPairs(scId).filter((p) => p.book.folder !== folder);
  const seriesVoices = pairs[0]?.character.voices ?? [];
  return {
    bookGains: seriesVoices.filter((sv) => !c.voices.some((x) => x.id === sv.id)),
    seriesGains: c.voices.filter((x) => !seriesVoices.some((sv) => sv.id === x.id)),
  };
}

// --- Writes ------------------------------------------------------------------------------------

function updateCharacter(folder: string, id: string, f: (c: Character) => Character): void {
  books.update((all) =>
    all.map((b) =>
      b.folder === folder
        ? { ...b, characters: b.characters.map((c) => (c.id === id ? f(c) : c)) }
        : b,
    ),
  );
}

/** Writes `voices` to every Character linked to `scId` (book by book, Series position order). */
function writeSeriesVoices(scId: string, voices: Voice[]): string[] {
  const titles: string[] = [];
  for (const p of linkedPairs(scId)) {
    updateCharacter(p.book.folder, p.character.id, (c) => {
      const kept = voices.map((x) => ({ ...x }));
      const rules = c.rules.filter((r) => kept.some((k) => k.id === r.voiceId));
      if (!rules.some((r) => r.kind === 'default') && kept[0])
        rules.unshift({ kind: 'default', voiceId: kept[0].id });
      return { ...c, voices: kept, rules };
    });
    titles.push(p.book.title);
  }
  return titles;
}

export function link(folder: string, characterId: string, scId: string): void {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const { bookGains } = mergePreview(folder, c, scId);
  const merged = [...c.voices, ...bookGains];
  updateCharacter(folder, characterId, (x) => ({ ...x, link: scId }));
  const titles = writeSeriesVoices(scId, merged);
  log(
    `Linked ${c.name} (${bookByFolder(folder).title}) to ${seriesChar(scId).label}; Voices written to ${titles.length} books`,
  );
}

export function shareAsNew(folder: string, characterId: string): SeriesCharacter {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const sc = { id: `sc-${Date.now().toString(36)}`, label: c.name };
  seriesChars.update((all) => [...all, sc]);
  updateCharacter(folder, characterId, (x) => ({ ...x, link: sc.id }));
  log(`New series character "${sc.label}" from ${bookByFolder(folder).title}`);
  return sc;
}

export function unlink(folder: string, characterId: string): void {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const sc = c.link;
  updateCharacter(folder, characterId, (x) => ({ ...x, link: null }));
  if (sc && !linkedPairs(sc).length) seriesChars.update((all) => all.filter((s) => s.id !== sc));
  log(
    `Unlinked ${c.name} in ${bookByFolder(folder).title}; it keeps its copy of ${c.voices.length} Voices`,
  );
}

export function dismiss(folder: string, characterId: string): void {
  dismissed.update((s) => new Set([...s, `${folder}|${characterId}`]));
  log(`Dismissed a suggestion in ${bookByFolder(folder).title}`);
}

export function editVoice(
  folder: string,
  characterId: string,
  voiceId: string,
  name: string,
): string[] {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const voices = c.voices.map((x) => (x.id === voiceId ? { ...x, name } : x));
  if (!c.link) {
    updateCharacter(folder, characterId, (x) => ({ ...x, voices }));
    log(`Renamed a Voice in ${bookByFolder(folder).title}`);
    return [bookByFolder(folder).title];
  }
  const titles = writeSeriesVoices(c.link, voices);
  log(`Renamed Voice to "${name}" in ${titles.join(', ')}`);
  return titles;
}

/** Books (other than `folder`) whose positional rules target the Voice: they refuse the delete. */
export function deleteBlockers(folder: string, c: Character, voiceId: string): string[] {
  if (!c.link) return [];
  return linkedPairs(c.link)
    .filter(
      (p) =>
        p.book.folder !== folder &&
        p.character.rules.some((r) => r.kind === 'positional' && r.voiceId === voiceId),
    )
    .map(
      (p) =>
        `${p.book.title} (${p.character.rules.find((r) => r.kind === 'positional' && r.voiceId === voiceId)!.where})`,
    );
}

export function deleteVoice(folder: string, characterId: string, voiceId: string): string[] {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const voices = c.voices.filter((x) => x.id !== voiceId);
  if (!c.link) {
    updateCharacter(folder, characterId, (x) => ({
      ...x,
      voices,
      rules: x.rules
        .filter((r) => r.voiceId !== voiceId)
        .concat(
          x.rules.some((r) => r.kind === 'default' && r.voiceId !== voiceId) || !voices[0]
            ? []
            : [{ kind: 'default', voiceId: voices[0].id }],
        ),
    }));
    return [bookByFolder(folder).title];
  }
  const titles = writeSeriesVoices(c.link, voices);
  log(`Deleted a shared Voice from ${titles.join(', ')}`);
  return titles;
}

export function push(folder: string, characterId: string): void {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const titles = writeSeriesVoices(c.link!, c.voices);
  log(
    `Pushed ${c.name}'s Voices from ${bookByFolder(folder).title} to ${titles.length - 1} other books`,
  );
}

export function take(folder: string, characterId: string): void {
  const c = bookByFolder(folder).characters.find((x) => x.id === characterId)!;
  const other = linkedPairs(c.link!).find((p) => p.book.folder !== folder)!;
  updateCharacter(folder, characterId, (x) => {
    const voices = other.character.voices.map((y) => ({ ...y }));
    return { ...x, voices, rules: x.rules.filter((r) => voices.some((y) => y.id === r.voiceId)) };
  });
  log(`Took the series' Voices into ${bookByFolder(folder).title}`);
}

// --- Series Voice editor (variant B, after feedback) ------------------------------------------

/** The series character open in the board's Voice editor. */
export const editing = signal<ReadonlySet<string>>(new Set());
export const isEditing = (id: string) => editing().has(id);
export function toggleEditing(id: string): void {
  const next = new Set(editing());
  if (!next.delete(id)) next.add(id);
  editing.set(next);
}

/** The series' Voices: the copy in the lowest-positioned linked book that doesn't differ. */
export function seriesVoices(scId: string): { voices: Voice[]; from: Book | null } {
  const pairs = linkedPairs(scId);
  const clean = pairs.find((p) => !differs(p.book.folder, p.character)) ?? pairs[0];
  return { voices: clean?.character.voices ?? [], from: clean?.book ?? null };
}

/** Where the edit is made from: the source book's Character, so the usual write order applies. */
function source(scId: string): { folder: string; character: Character } {
  const { from } = seriesVoices(scId);
  const p = linkedPairs(scId).find((x) => x.book.folder === from!.folder)!;
  return { folder: p.book.folder, character: p.character };
}

let voiceSeq = 1;
export function addSeriesVoice(scId: string, name: string, description: string): string[] {
  const { voices } = seriesVoices(scId);
  const titles = writeSeriesVoices(scId, [
    ...voices,
    { id: `v-new-${voiceSeq++}`, name, description, audio: `new-${voiceSeq}` },
  ]);
  log(`Added Voice "${name}" to ${titles.join(', ')}`);
  return titles;
}

export function renameSeriesVoice(scId: string, voiceId: string, name: string): string[] {
  const s = source(scId);
  return editVoice(s.folder, s.character.id, voiceId, name);
}

export function seriesDeleteBlockers(scId: string, voiceId: string): string[] {
  return linkedPairs(scId)
    .filter((p) => p.character.rules.some((r) => r.kind === 'positional' && r.voiceId === voiceId))
    .map(
      (p) =>
        `${p.book.title} (${p.character.rules.find((r) => r.kind === 'positional' && r.voiceId === voiceId)!.where})`,
    );
}

export function deleteSeriesVoice(scId: string, voiceId: string): string[] {
  const s = source(scId);
  return deleteVoice(s.folder, s.character.id, voiceId);
}

/** Sets one book's default rule: still that book's own rule, just set from the series board. */
export function setDefault(folder: string, characterId: string, voiceId: string): void {
  updateCharacter(folder, characterId, (c) => ({
    ...c,
    rules: [{ kind: 'default', voiceId }, ...c.rules.filter((r) => r.kind !== 'default')],
  }));
  log(
    `${bookByFolder(folder).title}: default Voice is now "${
      bookByFolder(folder)
        .characters.find((c) => c.id === characterId)!
        .voices.find((v) => v.id === voiceId)?.name
    }"`,
  );
}
