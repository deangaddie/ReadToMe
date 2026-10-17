import { describe, expect, it } from 'bun:test';
import type { NodeDto } from '@app/api';
import {
  type TreeNode,
  buildTree,
  chapterAncestry,
  chapterSequence,
  firstUnloaded,
} from './book-tree';

const node = (id: string, title: string | null = null): NodeDto => ({ id, title });

function shape(nodes: TreeNode[]): unknown[] {
  return nodes.map((n) =>
    n.children.length ? { [`${n.level}:${n.title}`]: shape(n.children) } : `${n.level}:${n.title}`,
  );
}

describe('buildTree', () => {
  it('one volume with one part collapses to its chapters', () => {
    const tree = buildTree([node('v1', 'Vol')], {
      v1: [node('p1', 'Part')],
      p1: [node('c1', 'One'), node('c2', 'Two')],
    });
    expect(shape(tree)).toEqual(['chapter:One', 'chapter:Two']);
  });

  it('one volume with several parts shows the parts as roots', () => {
    const tree = buildTree([node('v1')], {
      v1: [node('p1', 'A'), node('p2', 'B')],
      p1: [node('c1', 'One')],
    });
    expect(shape(tree)).toEqual([{ 'part:A': ['chapter:One'] }, 'part:B']);
    expect(tree[1]).toMatchObject({ expandable: true, loaded: false });
    expect(tree[0]!.loaded).toBe(true);
  });

  it('several volumes keep the volume level; a single part inside one collapses away', () => {
    const tree = buildTree([node('v1', 'I'), node('v2', 'II')], {
      v1: [node('p1')],
      p1: [node('c1', 'One')],
      v2: [node('p2', 'X'), node('p3', 'Y')],
    });
    expect(shape(tree)).toEqual([
      { 'volume:I': ['chapter:One'] },
      { 'volume:II': ['part:X', 'part:Y'] },
    ]);
  });

  it('a collapsed-away single part whose chapters are not loaded yet leaves the volume unloaded', () => {
    const tree = buildTree([node('v1', 'I'), node('v2', 'II')], { v1: [node('p1')] });
    expect(tree[0]).toMatchObject({ loaded: false, children: [] });
    expect(tree[0]!.loadTarget).toEqual({ level: 'part', id: 'p1' });
  });

  it('untitled nodes carry a placeholder title numbered by position', () => {
    const tree = buildTree([node('v1'), node('v2', '  ')], {
      v2: [node('p1'), node('p2')],
      p2: [node('c1'), node('c2')],
    });
    expect(tree.map((n) => n.title)).toEqual(['Untitled volume 1', 'Untitled volume 2']);
    expect(tree[1]!.children.map((n) => n.title)).toEqual(['Untitled part 1', 'Untitled part 2']);
    expect(tree[1]!.children[1]!.children.map((n) => n.title)).toEqual([
      'Untitled chapter 1',
      'Untitled chapter 2',
    ]);
    expect(tree.every((n) => n.untitled)).toBe(true);
  });

  it('a titled node is not untitled', () => {
    const [titled] = buildTree([node('v1', 'I'), node('v2')], {});
    expect(titled).toMatchObject({ title: 'I', untitled: false });
  });

  it('nodes know their stored title and their position among siblings', () => {
    const tree = buildTree([node('v1', 'I'), node('v2'), node('v3', 'III')], {
      v2: [node('p1', 'A'), node('p2')],
      p1: [node('c1'), node('c2', 'Two'), node('c3')],
    });
    expect(tree.map((n) => [n.rawTitle, n.isFirst, n.isLast])).toEqual([
      ['I', true, false],
      [null, false, false],
      ['III', false, true],
    ]);
    const parts = tree[1]!.children;
    expect(parts.map((n) => [n.isFirst, n.isLast])).toEqual([
      [true, false],
      [false, true],
    ]);
    expect(parts[0]!.children.map((n) => [n.rawTitle, n.isFirst, n.isLast])).toEqual([
      [null, true, false],
      ['Two', false, false],
      [null, false, true],
    ]);
  });

  it('chapters are leaves', () => {
    const [chapter] = buildTree([node('v1')], { v1: [node('p1')], p1: [node('c1')] });
    expect(chapter).toMatchObject({ level: 'chapter', expandable: false, loaded: true });
  });
});

describe('chapterSequence', () => {
  it('lists every loaded chapter in book order with its title', () => {
    const volumes = [node('v1'), node('v2')];
    const children = {
      v1: [node('p1'), node('p2')],
      p1: [node('c1', 'One')],
      p2: [node('c2')],
      v2: [node('p3')],
      p3: [node('c3', 'Three')],
    };
    expect(chapterSequence(volumes, children).map(({ volume: _v, part: _p, ...c }) => c)).toEqual([
      { id: 'c1', title: 'One', untitled: false, rawTitle: 'One', isFirst: true, isLast: true },
      {
        id: 'c2',
        title: 'Untitled chapter 1',
        untitled: true,
        rawTitle: null,
        isFirst: true,
        isLast: true,
      },
      { id: 'c3', title: 'Three', untitled: false, rawTitle: 'Three', isFirst: true, isLast: true },
    ]);
  });

  it("gives each chapter its position among its part's chapters, for the merge entries", () => {
    const children = { v1: [node('p1')], p1: [node('a'), node('b'), node('c')] };
    expect(
      chapterSequence([node('v1')], children).map(({ id, isFirst, isLast }) => [
        id,
        isFirst,
        isLast,
      ]),
    ).toEqual([
      ['a', true, false],
      ['b', false, false],
      ['c', false, true],
    ]);
  });

  it('the first chapter of each Volume and Part the tree shows carries that heading', () => {
    const volumes = [node('v1', 'Book One'), node('v2')];
    const children = {
      v1: [node('p1', 'Arrival'), node('p2')],
      p1: [node('c1'), node('c2')],
      p2: [node('c3')],
      v2: [node('p3', 'Lone part')],
      p3: [node('c4')],
    };
    const headings = chapterSequence(volumes, children).map(({ id, volume, part }) => ({
      id,
      volume,
      part,
    }));
    expect(headings).toEqual([
      {
        id: 'c1',
        volume: {
          id: 'v1',
          title: 'Book One',
          untitled: false,
          rawTitle: 'Book One',
          isFirst: true,
          isLast: false,
        },
        part: {
          id: 'p1',
          title: 'Arrival',
          untitled: false,
          rawTitle: 'Arrival',
          isFirst: true,
          isLast: false,
        },
      },
      { id: 'c2', volume: undefined, part: undefined },
      {
        id: 'c3',
        volume: undefined,
        part: {
          id: 'p2',
          title: 'Untitled part 2',
          untitled: true,
          rawTitle: null,
          isFirst: false,
          isLast: true,
        },
      },
      // A lone Part within a Volume is hidden in the tree, so it gets no heading either.
      {
        id: 'c4',
        volume: {
          id: 'v2',
          title: 'Untitled volume 2',
          untitled: true,
          rawTitle: null,
          isFirst: false,
          isLast: true,
        },
        part: undefined,
      },
    ]);
  });

  it('a single Volume gets no heading; a Volume heading waits for its first chapter', () => {
    const children = {
      v1: [node('p1', 'Empty'), node('p2', 'Full')],
      p1: [],
      p2: [node('c1')],
    };
    const [only] = chapterSequence([node('v1', 'Only')], children);
    expect(only).toMatchObject({ id: 'c1', part: { id: 'p2' } });
    expect(only?.volume).toBeUndefined();

    const volumes = [node('v1', 'I'), node('v2', 'II')];
    expect(chapterSequence(volumes, { ...children, v2: [] })).toMatchObject([
      { id: 'c1', volume: { id: 'v1' }, part: { id: 'p2' } },
    ]);
  });

  it('stops at the first unloaded gap so prev/next never skip unknown chapters', () => {
    const volumes = [node('v1'), node('v2')];
    const children = { v1: [node('p1')], p1: [node('c1')], v2: [node('p2')], p2: [node('c9')] };
    expect(chapterSequence(volumes, {}).length).toBe(0);
    expect(chapterSequence(volumes, children).map((c) => c.id)).toEqual(['c1', 'c9']);
    expect(
      chapterSequence(volumes, { v1: [node('p1')], p1: [node('c1')] }).map((c) => c.id),
    ).toEqual(['c1']);
  });
});

describe('firstUnloaded', () => {
  it('names the first parent in book order whose children are not loaded', () => {
    const volumes = [node('v1'), node('v2')];
    expect(firstUnloaded(volumes, {})).toEqual({ level: 'volume', id: 'v1' });
    expect(firstUnloaded(volumes, { v1: [node('p1')] })).toEqual({ level: 'part', id: 'p1' });
    expect(firstUnloaded(volumes, { v1: [node('p1')], p1: [] })).toEqual({
      level: 'volume',
      id: 'v2',
    });
    expect(firstUnloaded(volumes, { v1: [], v2: [] })).toBeNull();
  });
});

describe('chapterAncestry', () => {
  it('maps every loaded chapter to its part and volume', () => {
    const volumes = [node('v1'), node('v2')];
    const children = {
      v1: [node('p1'), node('p2')],
      p1: [node('c1')],
      p2: [node('c2')],
      v2: [node('p3')],
    };
    expect(chapterAncestry(volumes, children)).toEqual({
      c1: { partId: 'p1', volumeId: 'v1' },
      c2: { partId: 'p2', volumeId: 'v1' },
    });
  });
});
