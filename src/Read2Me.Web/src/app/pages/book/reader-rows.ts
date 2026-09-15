import {
  AudioReviewDto,
  ItemVoiceDto,
  NarratorDto,
  ParagraphDto,
  ParagraphItemDto,
  ParagraphItemType,
} from '@app/api';
import {
  AudioReviewInfo,
  ItemStatusEntry,
  OutcomeEntry,
  ParagraphStatusEntry,
} from '@app/live/live-messages';
import { SpeakerChipState } from '@app/ui/speaker-chip/speaker-chip';
import { SpeakerRosterEntry } from '@app/ui/speaker-menu/speaker-menu';
import { StatusKind } from '@app/ui/status-chip/status-chip';
import { ChapterParents } from './book-tree';

/**
 * What the reader rows show (design §6.3), as pure functions of the loaded data. Components bind
 * these; they never decide a label themselves, so the mode rules are table-tested once here.
 */

export const READER_MODES = ['read', 'speakers', 'audio'] as const;
export type ReaderMode = (typeof READER_MODES)[number];

export function parseMode(value: string | null | undefined): ReaderMode {
  return READER_MODES.find((m) => m === value) ?? 'read';
}

// ---- speakers -----------------------------------------------------------------------------------

export interface SpeakerContext {
  /** Character id → name, from the book overview roster. */
  names: Readonly<Record<string, string>>;
  narrator: NarratorDto | null;
}

export interface SpeakerDisplay {
  state: SpeakerChipState;
  name: string;
  characterId?: string;
}

/** Narration is decided by the host (`itemType`), not by comparing ids here (ADR-0006). */
function isNarration(item: ParagraphItemDto): boolean {
  return item.itemType === 'Narration';
}

const UNKNOWN: SpeakerDisplay = { state: 'unknown', name: 'Unknown' };

function narration(ctx: SpeakerContext): SpeakerDisplay {
  const narrator = ctx.narrator;
  return {
    state: 'narration',
    name: narrator?.isLinked ? `Narrator (${narrator.displayName})` : 'Narration',
  };
}

export function itemSpeaker(item: ParagraphItemDto, ctx: SpeakerContext): SpeakerDisplay {
  if (isNarration(item)) return narration(ctx);
  const id = item.characterId;
  const name = id ? ctx.names[id] : undefined;
  if (!id || name === undefined) return UNKNOWN;
  const linked = ctx.narrator?.isLinked && ctx.narrator.characterId === id;
  return { state: linked ? 'narrator-linked' : 'named', name, characterId: id };
}

/** One chip for the whole paragraph: Narration / Name / "Name +?" / Unknown / Mixed. */
export function paragraphSpeaker(paragraph: ParagraphDto, ctx: SpeakerContext): SpeakerDisplay {
  const dialog = paragraph.items.filter((i) => !i.isPause && !isNarration(i));
  if (dialog.length === 0) return narration(ctx);

  const speakers = dialog.map((i) => itemSpeaker(i, ctx));
  const known = new Map<string, SpeakerDisplay>();
  for (const s of speakers) if (s.characterId) known.set(s.characterId, s);
  const hasUnknown = speakers.some((s) => !s.characterId);

  if (known.size > 1) return { state: 'mixed', name: 'Mixed' };
  const [only] = known.values();
  if (!only) return UNKNOWN;
  return hasUnknown ? { ...only, name: `${only.name} +?` } : only;
}

/** Read mode's prose: the spoken items joined. */
export function paragraphText(paragraph: ParagraphDto): string {
  return paragraph.items
    .filter((i) => !i.isPause && i.text)
    .map((i) => i.text!.trim())
    .join(' ');
}

// ---- audio --------------------------------------------------------------------------------------

/** "Voice: Deep", "Narrator → Watson · Voice: Deep", "Voice: —"; empty until the map is loaded. */
export function voiceLine(voice: ItemVoiceDto | undefined): string {
  if (!voice) return '';
  const line = `Voice: ${voice.voiceName ?? '—'}`;
  return voice.narratedBy ? `Narrator → ${voice.narratedBy} · ${line}` : line;
}

export interface ChipView {
  status: StatusKind;
  label: string;
  icon?: string;
  tooltip?: string | undefined;
}

/** Queue state wins over a remembered outcome: a re-queued failure shows Queued. */
export function queueChip(
  entry: ParagraphStatusEntry | ItemStatusEntry | null | undefined,
  unfinishedLabel = 'Unfinished',
): ChipView | null {
  if (!entry) return null;
  if (entry.status === 'Queued') return { status: 'info', label: 'Queued', icon: 'schedule' };
  if (entry.status === 'Processing') return { status: 'busy', label: 'Processing' };
  const outcome = entry.outcome;
  if (!outcome) return null;
  const tooltip = outcome.reason ?? undefined;
  return outcome.kind === 'Failed'
    ? { status: 'error', label: 'Failed', tooltip }
    : { status: 'warn', label: unfinishedLabel, tooltip };
}

export function reviewChip(
  review: AudioReviewInfo | AudioReviewDto | null | undefined,
): ChipView | null {
  if (!review) return null;
  if (review.state === 'Dismissed') {
    return { status: 'neutral', label: 'Review dismissed', icon: 'rate_review' };
  }
  if (!review.normalizeOk) {
    return {
      status: 'error',
      label: 'Normalize failed',
      icon: 'graphic_eq',
      tooltip: review.normalizeReason ?? undefined,
    };
  }
  if (!review.verifyOk) {
    const wer =
      review.wer === null || review.wer === undefined
        ? null
        : `WER ${Math.round(review.wer * 100)}%`;
    const tooltip = [wer, review.verifyReason].filter(Boolean).join(' — ');
    return {
      status: 'warn',
      label: 'Verify failed',
      icon: 'rate_review',
      tooltip: tooltip || undefined,
    };
  }
  return null;
}

/** Everything a paragraph or item row reads besides its own data; one object per render pass. */
export interface RowContext {
  folder: string;
  mode: ReaderMode;
  speakers: SpeakerContext;
  paragraphStatus: Readonly<Record<string, ParagraphStatusEntry | null>>;
  itemStatus: Readonly<Record<string, ItemStatusEntry | null>>;
  voices: Readonly<Record<string, ItemVoiceDto>>;
  reviews: Readonly<Record<string, AudioReviewDto>>;
  /** Paragraph selection (ticket 12): on in Read and Speakers modes. */
  selectable: boolean;
  selected: ReadonlySet<string>;
  /** Item selection (ticket 13): on in Audio mode. */
  itemSelectable: boolean;
  selectedItems: ReadonlySet<string>;
  /** Narrator-only mode reads unattributed lines too, so they can be selected for audio. */
  narratorOnlyMode: boolean;
  /** Part and volume per loaded chapter, for the roll-up of a ticked row. */
  ancestry: Readonly<Record<string, ChapterParents>>;
  /** What the speaker menus offer; empty until the roster is known. */
  roster: readonly SpeakerRosterEntry[];
}

/** Queued or processing: the server rejects edits, so the row shows a lock (design §8). */
export function isBusy(entry: ParagraphStatusEntry | ItemStatusEntry | null | undefined): boolean {
  return entry?.status === 'Queued' || entry?.status === 'Processing';
}

/**
 * A settled outcome (Failed/Unfinished) the user may act on by hand — clear it on a paragraph,
 * retry the item; null while queued or clean.
 */
export function clearableOutcome(
  entry: ParagraphStatusEntry | ItemStatusEntry | null | undefined,
): OutcomeEntry | null {
  if (!entry || isBusy(entry)) return null;
  return entry.outcome ?? null;
}

/** The hub's per-item review wins once it has reported one; the REST map covers the rest. */
export function reviewOf(ctx: RowContext, itemId: string): AudioReviewInfo | AudioReviewDto | null {
  const live = ctx.itemStatus[itemId]?.review;
  return live !== undefined ? live : (ctx.reviews[itemId] ?? null);
}

// ---- rows ---------------------------------------------------------------------------------------

export interface LoadedChapterView {
  id: string;
  title: string;
  paragraphs: readonly ParagraphDto[];
}

/** Position among the chapter's paragraphs: the node menu hides merges at the ends. */
export interface RowPosition {
  isFirst: boolean;
  isLast: boolean;
}

export type ReaderRow =
  | { kind: 'chapter'; key: string; chapterId: string; title: string }
  | ({ kind: 'paragraph'; key: string; chapterId: string; paragraph: ParagraphDto } & RowPosition)
  | ({
      kind: 'pause';
      key: string;
      chapterId: string;
      paragraph: ParagraphDto;
      label: string;
    } & RowPosition);

const PAUSE_LABELS: Partial<Record<ParagraphItemType, string>> = {
  Pause: 'Pause',
  ParagraphPause: 'Paragraph pause',
  ChapterPause: 'Chapter pause',
  PartPause: 'Part pause',
  VolumePause: 'Volume pause',
};

export function pauseLabel(item: ParagraphItemDto | undefined): string {
  return (item && PAUSE_LABELS[item.itemType]) ?? 'Paragraph pause';
}

/** The viewport's rows: a header per chapter, then its paragraphs; Read mode hides pause paragraphs. */
export function buildRows(chapters: readonly LoadedChapterView[], mode: ReaderMode): ReaderRow[] {
  const rows: ReaderRow[] = [];
  for (const chapter of chapters) {
    rows.push({
      kind: 'chapter',
      key: `chapter:${chapter.id}`,
      chapterId: chapter.id,
      title: chapter.title,
    });
    chapter.paragraphs.forEach((paragraph, i, all) => {
      const position = { isFirst: i === 0, isLast: i === all.length - 1 };
      if (!paragraph.isPauseParagraph) {
        rows.push({
          kind: 'paragraph',
          key: `paragraph:${paragraph.id}`,
          chapterId: chapter.id,
          paragraph,
          ...position,
        });
      } else if (mode !== 'read') {
        rows.push({
          kind: 'pause',
          key: `pause:${paragraph.id}`,
          chapterId: chapter.id,
          paragraph,
          label: pauseLabel(paragraph.items[0]),
          ...position,
        });
      }
    });
  }
  return rows;
}
