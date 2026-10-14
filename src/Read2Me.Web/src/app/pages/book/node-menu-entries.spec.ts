import { describe, expect, it } from 'bun:test';
import {
  type MenuEntryId,
  type NodeMenuKind,
  type NodeMenuTarget,
  menuEntries,
  mergeDirectionOf,
  nodeInsertOf,
  pauseInsertOf,
} from './node-menu-entries';

function target(kind: NodeMenuKind, overrides: Partial<NodeMenuTarget> = {}): NodeMenuTarget {
  return { kind, id: 'x', text: null, isFirst: false, isLast: false, ...overrides };
}

const ids = (t: NodeMenuTarget): MenuEntryId[] => menuEntries(t).map((e) => e.id);
const PAUSES = ['Pause', 'ParagraphPause', 'ChapterPause', 'PartPause', 'VolumePause'];
const NODE_INSERT_IDS: MenuEntryId[] = [
  'insert-node:Before',
  'insert-node:After',
  'insert-node:AtStart',
  'insert-node:AtEnd',
];
const pauseIds = (position: 'before' | 'after'): MenuEntryId[] =>
  PAUSES.map((k) => `pause-${position}:${k}` as MenuEntryId);

/** Research §3 "Node menu": the exact entry set per kind, with a middle sibling's merges. */
describe('menuEntries matrix', () => {
  it('volume: edit title, merges, insert volume before/after and part at start/end, delete', () => {
    expect(ids(target('volume'))).toEqual([
      'edit-title',
      'merge-previous',
      'merge-next',
      ...NODE_INSERT_IDS,
      'delete',
    ]);
  });

  it('part: edit title, split volume, merges, insert part before/after and chapter at start/end, delete', () => {
    const entries = menuEntries(target('part'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-title',
      'split',
      'merge-previous',
      'merge-next',
      ...NODE_INSERT_IDS,
      'delete',
    ]);
    expect(entries[1]!.label).toBe('Split volume');
    expect(entries.at(-1)!.label).toBe('Delete part');
  });

  it('chapter: edit title, split part, merges, insert chapter before/after and paragraph at start/end, delete', () => {
    const entries = menuEntries(target('chapter'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-title',
      'split',
      'merge-previous',
      'merge-next',
      ...NODE_INSERT_IDS,
      'delete',
    ]);
    expect(entries[1]!.label).toBe('Split part');
  });

  it('paragraph: no edit; split chapter, merges, insert paragraph before/after, delete', () => {
    const entries = menuEntries(target('paragraph'));
    expect(entries.map((e) => e.id)).toEqual([
      'split',
      'merge-previous',
      'merge-next',
      'insert-node:Before',
      'insert-node:After',
      'delete',
    ]);
    expect(entries[0]!.label).toBe('Split chapter');
  });

  it('node inserts sit in one flat Insert group: own level beside, one level down inside, never two', () => {
    const inserts = (t: NodeMenuTarget) =>
      menuEntries(t)
        .filter((e) => e.group === 'insert')
        .map((e) => e.label);
    expect(inserts(target('volume'))).toEqual([
      'Insert volume before',
      'Insert volume after',
      'Insert part at start',
      'Insert part at end',
    ]);
    expect(inserts(target('part'))).toEqual([
      'Insert part before',
      'Insert part after',
      'Insert chapter at start',
      'Insert chapter at end',
    ]);
    expect(inserts(target('chapter'))).toEqual([
      'Insert chapter before',
      'Insert chapter after',
      'Insert paragraph at start',
      'Insert paragraph at end',
    ]);
    expect(inserts(target('paragraph'))).toEqual(['Insert paragraph before', 'Insert paragraph after']);
    expect(inserts(target('pause-paragraph'))).toEqual(['Insert paragraph before', 'Insert paragraph after']);
  });

  it('an item offers no node inserts', () => {
    expect(ids(target('item')).filter((id) => id.startsWith('insert-node:'))).toEqual([]);
  });

  it('item: edit text, add cue, split paragraph, merges, insert item before/after, five pauses each way, delete', () => {
    const entries = menuEntries(target('item'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-text',
      'add-cue',
      'split',
      'merge-previous',
      'merge-next',
      'insert-before',
      'insert-after',
      ...pauseIds('before'),
      ...pauseIds('after'),
      'delete',
    ]);
    expect(entries[2]!.label).toBe('Split paragraph');
    expect(entries.filter((e) => e.group === 'pause-before').map((e) => e.label)).toEqual([
      'Pause',
      'Paragraph pause',
      'Chapter pause',
      'Part pause',
      'Volume pause',
    ]);
  });

  it('pause item: insert item entries are hidden, pause inserts stay', () => {
    const list = ids(target('item', { isPause: true }));
    expect(list).not.toContain('insert-before');
    expect(list).not.toContain('insert-after');
    expect(list).toEqual(expect.arrayContaining(pauseIds('before')));
  });

  it('item without a cue offers Add delivery cue…', () => {
    const entries = menuEntries(target('item', { cue: null }));
    expect(entries.filter((e) => e.id.endsWith('-cue')).map((e) => [e.id, e.label, e.group])).toEqual([
      ['add-cue', 'Add delivery cue…', 'edit'],
    ]);
  });

  it('item with a cue offers Edit delivery cue… and Remove delivery cue instead', () => {
    const entries = menuEntries(target('item', { cue: 'whispered' }));
    expect(entries.filter((e) => e.group === 'edit').map((e) => [e.id, e.label])).toEqual([
      ['edit-text', 'Edit text'],
      ['edit-cue', 'Edit delivery cue…'],
      ['remove-cue', 'Remove delivery cue'],
    ]);
  });

  it('pause item and every other kind: no cue entries', () => {
    const cueIds = (t: NodeMenuTarget) => ids(t).filter((id) => id.endsWith('-cue'));
    expect(cueIds(target('item', { isPause: true }))).toEqual([]);
    expect(cueIds(target('item', { isPause: true, cue: 'whispered' }))).toEqual([]);
    for (const kind of ['volume', 'part', 'chapter', 'paragraph', 'pause-paragraph'] as const) {
      expect(cueIds(target(kind, { cue: 'x' }))).toEqual([]);
    }
  });

  it('pause paragraph: insert paragraph before/after, delete pause', () => {
    const entries = menuEntries(target('pause-paragraph'));
    expect(entries.map((e) => e.id)).toEqual(['insert-node:Before', 'insert-node:After', 'delete']);
    expect(entries.at(-1)!.label).toBe('Delete pause');
  });

  it('only delete is destructive', () => {
    for (const kind of ['volume', 'part', 'chapter', 'paragraph', 'item', 'pause-paragraph'] as const) {
      const destructive = menuEntries(target(kind)).filter((e) => e.destructive);
      expect(destructive.map((e) => e.id)).toEqual(['delete']);
    }
  });
});

describe('menuEntries position', () => {
  const kinds: NodeMenuKind[] = ['volume', 'part', 'chapter', 'paragraph', 'item'];

  it.each(kinds)('%s: first sibling hides merge with previous', (kind) => {
    const list = ids(target(kind, { isFirst: true }));
    expect(list).not.toContain('merge-previous');
    expect(list).toContain('merge-next');
  });

  it.each(kinds)('%s: last sibling hides merge with next', (kind) => {
    const list = ids(target(kind, { isLast: true }));
    expect(list).toContain('merge-previous');
    expect(list).not.toContain('merge-next');
  });

  it('a lone volume has no Delete but keeps its inserts: a book keeps at least one volume', () => {
    expect(ids(target('volume', { isFirst: true, isLast: true }))).toEqual([
      'edit-title',
      ...NODE_INSERT_IDS,
    ]);
  });

  it.each([
    ['first', { isFirst: true }],
    ['middle', {}],
    ['last', { isLast: true }],
  ])('a %s volume among several keeps Delete', (_, position) => {
    expect(ids(target('volume', position))).toContain('delete');
  });

  it.each(kinds.filter((k) => k !== 'volume'))('%s: an only child keeps Delete', (kind) => {
    expect(ids(target(kind, { isFirst: true, isLast: true }))).toContain('delete');
  });

  it.each(kinds)('%s: an only child has no merge entries', (kind) => {
    const list = ids(target(kind, { isFirst: true, isLast: true }));
    expect(list).not.toContain('merge-previous');
    expect(list).not.toContain('merge-next');
  });
});

describe('entry decoding', () => {
  it('reads merge directions', () => {
    expect(mergeDirectionOf('merge-previous')).toBe('Previous');
    expect(mergeDirectionOf('merge-next')).toBe('Next');
    expect(mergeDirectionOf('split')).toBeNull();
  });

  it('reads pause inserts', () => {
    expect(pauseInsertOf('pause-before:ChapterPause')).toEqual({
      position: 'Before',
      kind: 'ChapterPause',
    });
    expect(pauseInsertOf('pause-after:Pause')).toEqual({ position: 'After', kind: 'Pause' });
    expect(pauseInsertOf('insert-after')).toBeNull();
  });

  it('reads node insert placements', () => {
    expect(nodeInsertOf('insert-node:AtStart')).toBe('AtStart');
    expect(nodeInsertOf('insert-node:After')).toBe('After');
    expect(nodeInsertOf('insert-after')).toBeNull();
  });
});

describe('menuEntries selection shortcuts (tickets 12 + 13)', () => {
  it('a tree node in a paragraph selection leads with Select unprocessed and Attribute unprocessed', () => {
    for (const kind of ['volume', 'part', 'chapter'] as const) {
      const entries = menuEntries(target(kind, { selection: 'paragraphs' }));
      expect(entries.slice(0, 2).map((e) => [e.id, e.group])).toEqual([
        ['select-unprocessed', 'select'],
        ['attribute-node', 'select'],
      ]);
      expect(entries.find((e) => e.group === 'edit')!.id).toBe('edit-title');
    }
  });

  it('a tree node in an item selection leads with Select needs audio and Generate audio', () => {
    for (const kind of ['volume', 'part', 'chapter'] as const) {
      const entries = menuEntries(target(kind, { selection: 'items' }));
      expect(entries.slice(0, 2).map((e) => [e.id, e.group])).toEqual([
        ['select-needs-audio', 'select'],
        ['generate-audio-node', 'select'],
      ]);
      expect(entries[2]!.id).toBe('edit-title');
      expect(ids(target(kind, { selection: 'items' }))).not.toContain('select-unprocessed');
    }
  });

  it('a part or chapter in a paragraph selection offers Release narration after the shortcuts', () => {
    for (const kind of ['part', 'chapter'] as const) {
      const entries = menuEntries(target(kind, { selection: 'paragraphs' }));
      const release = entries[2]!;
      expect([release.id, release.label, release.group]).toEqual([
        'release-narration',
        'Release narration',
        'release',
      ]);
      expect(release.destructive).toBeUndefined();
      expect(entries[3]!.id).toBe('edit-title');
    }
  });

  it('Release narration is nowhere else: volumes, item selections, rows, pauses', () => {
    expect(ids(target('volume', { selection: 'paragraphs' }))).not.toContain('release-narration');
    for (const kind of ['part', 'chapter'] as const) {
      expect(ids(target(kind, { selection: 'items' }))).not.toContain('release-narration');
      expect(ids(target(kind))).not.toContain('release-narration');
    }
    for (const kind of ['paragraph', 'item', 'pause-paragraph'] as const) {
      expect(ids(target(kind, { selection: 'paragraphs' }))).not.toContain('release-narration');
    }
  });

  it('a narration-only paragraph offers Release narration first; other rows never do', () => {
    const entries = menuEntries(target('paragraph', { narrationOnly: true }));
    expect(entries.map((e) => [e.id, e.group])).toEqual([
      ['release-narration', 'release'],
      ['split', 'structure'],
      ['merge-previous', 'structure'],
      ['merge-next', 'structure'],
      ['insert-node:Before', 'insert'],
      ['insert-node:After', 'insert'],
      ['delete', 'delete'],
    ]);
    expect(entries[0]!.label).toBe('Release narration');
    expect(ids(target('paragraph'))).not.toContain('release-narration');
    expect(ids(target('item', { narrationOnly: true }))).not.toContain('release-narration');
    expect(ids(target('pause-paragraph', { narrationOnly: true }))).not.toContain(
      'release-narration',
    );
  });

  it('rows never get them, whatever the selection', () => {
    expect(ids(target('paragraph', { selection: 'paragraphs' }))).not.toContain('select-unprocessed');
    expect(ids(target('item', { selection: 'items' }))).not.toContain('generate-audio-node');
  });
});
