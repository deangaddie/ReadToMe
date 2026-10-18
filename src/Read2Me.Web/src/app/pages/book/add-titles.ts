import type { BookCommand } from '@app/api';

/**
 * The pure side of Add titles (guidance-primitives spec §5): which levels add navigation, what an
 * unticked or absent level says, the command per level and the toast once they have landed.
 */
export type TitleLevel = 'book' | 'volume' | 'part' | 'chapter';

/** The book's shape, from its overview. */
export interface BookShape {
  volumes: number;
  parts: number;
  chapters: number;
}

/** The levels in the order Apply sends them, each with its command and its muted lines. */
export const TITLE_LEVELS: readonly {
  level: TitleLevel;
  label: string;
  command: BookCommand;
  /** What listeners lose when the level is unticked. */
  lost: string;
  /** Why the level is absent when the book has nothing at it; the book title always applies. */
  missing: string | null;
}[] = [
  {
    level: 'book',
    label: 'Book title',
    command: { type: 'AddBookTitle' },
    lost: "Listeners won't hear the book's title at the start.",
    missing: null,
  },
  {
    level: 'volume',
    label: 'Volume titles',
    command: { type: 'AddVolumeTitles' },
    lost: "Listeners won't hear volume names as each volume starts.",
    missing: 'One volume, so no volume titles.',
  },
  {
    level: 'part',
    label: 'Part titles',
    command: { type: 'AddPartTitles' },
    lost: "Listeners won't hear part names as each part starts.",
    missing: 'One part per volume, so no part titles.',
  },
  {
    level: 'chapter',
    label: 'Chapter titles',
    command: { type: 'AddChapterTitles' },
    lost: "Listeners won't hear chapter names as each chapter starts.",
    missing: 'One chapter per part, so no chapter titles.',
  },
];

/** Which levels add navigation: only those a listener could otherwise not tell apart. */
export function availableLevels(shape: BookShape): Record<TitleLevel, boolean> {
  return {
    book: true,
    volume: shape.volumes > 1,
    part: shape.parts > shape.volumes,
    chapter: shape.chapters > shape.parts,
  };
}

/** The command that adds one level's titles. */
export function titleCommand(level: TitleLevel): BookCommand {
  return TITLE_LEVELS.find((l) => l.level === level)!.command;
}

/** "Added book and chapter titles": the toast once the levels have landed. */
export function addedTitlesMessage(levels: readonly TitleLevel[]): string {
  const names =
    levels.length === 1 ? levels[0] : `${levels.slice(0, -1).join(', ')} and ${levels.at(-1)}`;
  return `Added ${names} title${levels.length === 1 && levels[0] === 'book' ? '' : 's'}`;
}
