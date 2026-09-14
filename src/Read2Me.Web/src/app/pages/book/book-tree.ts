import { NodeDto, NodeLevel } from '@app/api';

/** Loaded children per parent id: volume id → parts, part id → chapters. Absent = not loaded yet. */
export type ChildrenMap = Readonly<Record<string, readonly NodeDto[] | undefined>>;

export interface NodeRef {
  level: NodeLevel;
  id: string;
}

export interface TreeNode {
  id: string;
  level: NodeLevel;
  title: string;
  /** The stored title, null when untitled ({@link title} then carries the positional fallback). */
  rawTitle: string | null;
  /** Position among siblings of the same level: the node menu hides merges at the ends. */
  isFirst: boolean;
  isLast: boolean;
  expandable: boolean;
  /** Children are known (possibly none). */
  loaded: boolean;
  /** What to load to fill {@link children} — the node itself, or the single part it collapses. */
  loadTarget: NodeRef;
  children: TreeNode[];
}

export interface ChapterRef {
  id: string;
  title: string;
}

const LEVEL_WORD: Record<NodeLevel, string> = {
  volume: 'Volume',
  part: 'Part',
  chapter: 'Chapter',
};

/** A node's title, or its level and 1-based position among its siblings. */
export function nodeTitle(node: NodeDto, level: NodeLevel, index: number): string {
  return node.title?.trim() ? node.title : `${LEVEL_WORD[level]} ${index + 1}`;
}

/**
 * The structure tree (design §6.3) from what is loaded. A single volume and a single part add no
 * navigation, so they collapse away: one volume shows its parts (or chapters) as roots, and a
 * volume with one part shows that part's chapters directly.
 */
export function buildTree(volumes: readonly NodeDto[], children: ChildrenMap): TreeNode[] {
  const volumeNodes = volumes.map((v, i) => volumeNode(v, i, volumes.length, children));
  const [only] = volumeNodes;
  if (only && volumeNodes.length === 1) return only.children;
  return volumeNodes;
}

function identity(node: NodeDto, level: NodeLevel, index: number, count: number) {
  return {
    id: node.id,
    level,
    title: nodeTitle(node, level, index),
    rawTitle: node.title,
    isFirst: index === 0,
    isLast: index === count - 1,
  };
}

function volumeNode(volume: NodeDto, index: number, count: number, children: ChildrenMap): TreeNode {
  const parts = children[volume.id];
  const base = { ...identity(volume, 'volume', index, count), expandable: true };
  if (!parts) {
    return { ...base, loaded: false, loadTarget: { level: 'volume', id: volume.id }, children: [] };
  }
  const [onlyPart] = parts;
  if (onlyPart && parts.length === 1) {
    const chapters = children[onlyPart.id];
    return {
      ...base,
      loaded: chapters !== undefined,
      loadTarget: { level: 'part', id: onlyPart.id },
      children: (chapters ?? []).map((c, i, all) => chapterNode(c, i, all.length)),
    };
  }
  return {
    ...base,
    loaded: true,
    loadTarget: { level: 'volume', id: volume.id },
    children: parts.map((p, i) => partNode(p, i, parts.length, children)),
  };
}

function partNode(part: NodeDto, index: number, count: number, children: ChildrenMap): TreeNode {
  const chapters = children[part.id];
  return {
    ...identity(part, 'part', index, count),
    expandable: true,
    loaded: chapters !== undefined,
    loadTarget: { level: 'part', id: part.id },
    children: (chapters ?? []).map((c, i, all) => chapterNode(c, i, all.length)),
  };
}

function chapterNode(chapter: NodeDto, index: number, count: number): TreeNode {
  return {
    ...identity(chapter, 'chapter', index, count),
    expandable: false,
    loaded: true,
    loadTarget: { level: 'chapter', id: chapter.id },
    children: [],
  };
}

/**
 * Chapters in book order, up to the first volume or part whose children are not loaded — so the
 * chapter after the last one listed is either unknown (load {@link firstUnloaded}) or the end.
 */
export function chapterSequence(volumes: readonly NodeDto[], children: ChildrenMap): ChapterRef[] {
  const out: ChapterRef[] = [];
  for (const volume of volumes) {
    const parts = children[volume.id];
    if (!parts) return out;
    for (const part of parts) {
      const chapters = children[part.id];
      if (!chapters) return out;
      chapters.forEach((c, i) => out.push({ id: c.id, title: nodeTitle(c, 'chapter', i) }));
    }
  }
  return out;
}

/** The first volume or part, in book order, whose children are not loaded; null when all are. */
export function firstUnloaded(volumes: readonly NodeDto[], children: ChildrenMap): NodeRef | null {
  for (const volume of volumes) {
    const parts = children[volume.id];
    if (!parts) return { level: 'volume', id: volume.id };
    for (const part of parts) {
      if (!children[part.id]) return { level: 'part', id: part.id };
    }
  }
  return null;
}
