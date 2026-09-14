import {
  MenuEntryId,
  NodeMenuKind,
  NodeMenuTarget,
  menuEntries,
  mergeDirectionOf,
  pauseInsertOf,
} from './node-menu-entries';

function target(kind: NodeMenuKind, overrides: Partial<NodeMenuTarget> = {}): NodeMenuTarget {
  return { kind, id: 'x', text: null, isFirst: false, isLast: false, ...overrides };
}

const ids = (t: NodeMenuTarget): MenuEntryId[] => menuEntries(t).map((e) => e.id);
const PAUSES = ['Pause', 'ParagraphPause', 'ChapterPause', 'PartPause', 'VolumePause'];
const pauseIds = (position: 'before' | 'after'): MenuEntryId[] =>
  PAUSES.map((k) => `pause-${position}:${k}` as MenuEntryId);

/** Research §3 "Node menu": the exact entry set per kind, with a middle sibling's merges. */
describe('menuEntries matrix', () => {
  it('volume: edit title, merge prev/next, delete', () => {
    expect(ids(target('volume'))).toEqual([
      'edit-title',
      'merge-previous',
      'merge-next',
      'delete',
    ]);
  });

  it('part: edit title, split volume, merges, delete', () => {
    const entries = menuEntries(target('part'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-title',
      'split',
      'merge-previous',
      'merge-next',
      'delete',
    ]);
    expect(entries[1]!.label).toBe('Split volume');
    expect(entries.at(-1)!.label).toBe('Delete part');
  });

  it('chapter: edit title, split part, merges, delete', () => {
    const entries = menuEntries(target('chapter'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-title',
      'split',
      'merge-previous',
      'merge-next',
      'delete',
    ]);
    expect(entries[1]!.label).toBe('Split part');
  });

  it('paragraph: no edit; split chapter, merges, delete', () => {
    const entries = menuEntries(target('paragraph'));
    expect(entries.map((e) => e.id)).toEqual(['split', 'merge-previous', 'merge-next', 'delete']);
    expect(entries[0]!.label).toBe('Split chapter');
  });

  it('item: edit text, split paragraph, merges, insert item before/after, five pauses each way, delete', () => {
    const entries = menuEntries(target('item'));
    expect(entries.map((e) => e.id)).toEqual([
      'edit-text',
      'split',
      'merge-previous',
      'merge-next',
      'insert-before',
      'insert-after',
      ...pauseIds('before'),
      ...pauseIds('after'),
      'delete',
    ]);
    expect(entries[1]!.label).toBe('Split paragraph');
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

  it('pause paragraph: delete pause only', () => {
    const entries = menuEntries(target('pause-paragraph'));
    expect(entries.map((e) => e.id)).toEqual(['delete']);
    expect(entries[0]!.label).toBe('Delete pause');
  });

  it('only delete is destructive', () => {
    for (const kind of ['volume', 'part', 'chapter', 'paragraph', 'item', 'pause-paragraph'] as const) {
      const destructive = menuEntries(target(kind)).filter((e) => e.destructive);
      expect(destructive.map((e) => e.id)).toEqual(['delete']);
    }
  });
});

describe('menuEntries position', () => {
  const kinds = ['volume', 'part', 'chapter', 'paragraph', 'item'] as const;

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
});
