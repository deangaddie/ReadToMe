import type { NodeDto, NodeLevel } from '@app/api';

/** Loaded children per parent id: volume id → parts, part id → chapters. Absent = not loaded yet. */
export type ChildrenMap = Readonly<Record<string, readonly NodeDto[] | undefined>>;

export interface NodeRef {
  level: NodeLevel;
  id: string;
}

export interface TreeNode extends NodeTitle {
  id: string;
  level: NodeLevel;
  /** The stored title, null when never given one. */
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

export interface ChapterRef extends NodeTitle {
  id: string;
  /** The stored title, null when never given one: the Node menu edits it, never the placeholder. */
  rawTitle: string | null;
  /** Position among its part's chapters: the Node menu hides merges at the ends. */
  isFirst: boolean;
  isLast: boolean;
}

/** The title a node shows: its stored one, or a placeholder title when it has none. */
export interface NodeTitle {
  /** The stored title, or the placeholder title when {@link untitled}. */
  title: string;
  /** No stored title (null or blank): {@link title} is a placeholder, shown set apart. */
  untitled: boolean;
}

/** The class that sets a placeholder title apart from a real one; empty for a real title. */
export function placeholderTitleClass({ untitled }: NodeTitle): string {
  return untitled ? 'r2m-placeholder-title' : '';
}

/** A node's title, or the placeholder "Untitled <level> <n>", n its 1-based position among siblings. */
export function nodeTitle(node: NodeDto, level: NodeLevel, index: number): NodeTitle {
  return node.title?.trim()
    ? { title: node.title, untitled: false }
    : { title: `Untitled ${level} ${index + 1}`, untitled: true };
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
    ...nodeTitle(node, level, index),
    rawTitle: node.title,
    isFirst: index === 0,
    isLast: index === count - 1,
  };
}

function volumeNode(
  volume: NodeDto,
  index: number,
  count: number,
  children: ChildrenMap,
): TreeNode {
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
      chapters.forEach((c, i) =>
        out.push({
          id: c.id,
          ...nodeTitle(c, 'chapter', i),
          rawTitle: c.title,
          isFirst: i === 0,
          isLast: i === chapters.length - 1,
        }),
      );
    }
  }
  return out;
}

/** The part and volume a chapter rolls up into. */
export interface ChapterParents {
  partId: string;
  volumeId: string;
}

/** {@link ChapterParents} for every chapter whose structure is loaded. */
export function chapterAncestry(
  volumes: readonly NodeDto[],
  children: ChildrenMap,
): Record<string, ChapterParents> {
  const out: Record<string, ChapterParents> = {};
  for (const volume of volumes) {
    for (const part of children[volume.id] ?? []) {
      for (const chapter of children[part.id] ?? []) {
        out[chapter.id] = { partId: part.id, volumeId: volume.id };
      }
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
