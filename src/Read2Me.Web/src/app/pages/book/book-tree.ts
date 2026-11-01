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

/** A Volume, Part or Chapter as a Book page heading shows it. */
export interface HeadingRef extends NodeTitle {
  id: string;
  /** The stored title, null when never given one: the Node menu edits it, never the placeholder. */
  rawTitle: string | null;
  /** Position among its siblings: the Node menu hides merges at the ends. */
  isFirst: boolean;
  isLast: boolean;
}

export interface ChapterRef extends HeadingRef {
  /** Set on the first chapter of a Volume the tree shows: its heading goes above this chapter's. */
  volume?: HeadingRef;
  /** Set on the first chapter of a Part the tree shows: its heading goes above this chapter's. */
  part?: HeadingRef;
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
    : { title: `Untitled ${level.toLowerCase()} ${index + 1}`, untitled: true };
}

/**
 * Whether the tree (and so the Book page) shows a level: a lone volume, or a lone part within a
 * volume, adds no navigation and collapses away.
 */
function showsLevel(siblings: readonly unknown[]): boolean {
  return siblings.length > 1;
}

/**
 * The structure tree (design §6.3) from what is loaded. A single volume and a single part add no
 * navigation, so they collapse away: one volume shows its parts (or chapters) as roots, and a
 * volume with one part shows that part's chapters directly.
 */
export function buildTree(volumes: readonly NodeDto[], children: ChildrenMap): TreeNode[] {
  const volumeNodes = volumes.map((v, i) => volumeNode(v, i, volumes.length, children));
  const [only] = volumeNodes;
  if (only && !showsLevel(volumeNodes)) return only.children;
  return volumeNodes;
}

function headingRef(node: NodeDto, level: NodeLevel, index: number, count: number): HeadingRef {
  return {
    id: node.id,
    ...nodeTitle(node, level, index),
    rawTitle: node.title,
    isFirst: index === 0,
    isLast: index === count - 1,
  };
}

function identity(node: NodeDto, level: NodeLevel, index: number, count: number) {
  return { ...headingRef(node, level, index, count), level };
}

function volumeNode(
  volume: NodeDto,
  index: number,
  count: number,
  children: ChildrenMap,
): TreeNode {
  const parts = children[volume.id];
  const base = { ...identity(volume, 'Volume', index, count), expandable: true };
  if (!parts) {
    return { ...base, loaded: false, loadTarget: { level: 'Volume', id: volume.id }, children: [] };
  }
  const [onlyPart] = parts;
  if (onlyPart && !showsLevel(parts)) {
    const chapters = children[onlyPart.id];
    return {
      ...base,
      loaded: chapters !== undefined,
      loadTarget: { level: 'Part', id: onlyPart.id },
      children: (chapters ?? []).map((c, i, all) => chapterNode(c, i, all.length)),
    };
  }
  return {
    ...base,
    loaded: true,
    loadTarget: { level: 'Volume', id: volume.id },
    children: parts.map((p, i) => partNode(p, i, parts.length, children)),
  };
}

function partNode(part: NodeDto, index: number, count: number, children: ChildrenMap): TreeNode {
  const chapters = children[part.id];
  return {
    ...identity(part, 'Part', index, count),
    expandable: true,
    loaded: chapters !== undefined,
    loadTarget: { level: 'Part', id: part.id },
    children: (chapters ?? []).map((c, i, all) => chapterNode(c, i, all.length)),
  };
}

function chapterNode(chapter: NodeDto, index: number, count: number): TreeNode {
  return {
    ...identity(chapter, 'Chapter', index, count),
    expandable: false,
    loaded: true,
    loadTarget: { level: 'Chapter', id: chapter.id },
    children: [],
  };
}

/**
 * Chapters in book order, up to the first volume or part whose children are not loaded — so the
 * chapter after the last one listed is either unknown (load {@link firstUnloaded}) or the end.
 * The first chapter of a Volume or Part carries that parent's heading, but only for a level the
 * tree shows ({@link showsLevel}).
 */
export function chapterSequence(volumes: readonly NodeDto[], children: ChildrenMap): ChapterRef[] {
  const out: ChapterRef[] = [];
  for (const [v, volume] of volumes.entries()) {
    const parts = children[volume.id];
    if (!parts) return out;
    let volumeHeading = showsLevel(volumes)
      ? headingRef(volume, 'Volume', v, volumes.length)
      : undefined;
    for (const [p, part] of parts.entries()) {
      const chapters = children[part.id];
      if (!chapters) return out;
      let partHeading = showsLevel(parts) ? headingRef(part, 'Part', p, parts.length) : undefined;
      chapters.forEach((c, i) => {
        out.push({
          ...headingRef(c, 'Chapter', i, chapters.length),
          ...(volumeHeading && { volume: volumeHeading }),
          ...(partHeading && { part: partHeading }),
        });
        volumeHeading = partHeading = undefined;
      });
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
    if (!parts) return { level: 'Volume', id: volume.id };
    for (const part of parts) {
      if (!children[part.id]) return { level: 'Part', id: part.id };
    }
  }
  return null;
}
