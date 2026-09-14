import { NodeDto } from '@app/api';
import { TreeNode, buildTree, chapterSequence, firstUnloaded } from './book-tree';

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

  it('untitled nodes are numbered by position', () => {
    const tree = buildTree([node('v1'), node('v2', '  ')], {
      v2: [node('p1'), node('p2')],
      p2: [node('c1'), node('c2')],
    });
    expect(tree.map((n) => n.title)).toEqual(['Volume 1', 'Volume 2']);
    expect(tree[1]!.children.map((n) => n.title)).toEqual(['Part 1', 'Part 2']);
    expect(tree[1]!.children[1]!.children.map((n) => n.title)).toEqual(['Chapter 1', 'Chapter 2']);
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
    expect(chapterSequence(volumes, children)).toEqual([
      { id: 'c1', title: 'One' },
      { id: 'c2', title: 'Chapter 1' },
      { id: 'c3', title: 'Three' },
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
