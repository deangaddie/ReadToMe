import { BookFacet, Receipt, hasFacet } from '@app/live/live-messages';

/** What the reader holds for one loaded chapter, as far as matching a receipt goes. */
export interface LoadedChapter {
  chapterId: string;
  paragraphIds: ReadonlySet<string>;
  itemIds: ReadonlySet<string>;
}

export interface ReceiptContext {
  /** Highest revision the reader has accounted for; null before any baseline is known. */
  lastRevision: number | null;
  /** Loaded chapters, in reading order. */
  loaded: readonly LoadedChapter[];
  /** The chapter at the top of the viewport. */
  visibleChapterId: string | null;
}

export interface ReceiptPlan {
  /** Chapters whose paragraphs (and voices) must be read again. */
  reloadChapters: string[];
  /** Loaded chapters whose voice map alone must be read again. */
  reloadVoices: string[];
  /** Volumes, roster and the loaded tree children. */
  reloadOverview: boolean;
  reloadReviews: boolean;
  /** Evict loaded chapters other than those reloaded — their content can no longer be trusted. */
  dropOtherChapters: boolean;
  /** "Book updated elsewhere". */
  toast: boolean;
  /** The receipt skipped at least one revision. */
  gap: boolean;
}

/** Facets that change what a paragraph row shows. */
const CONTENT_FACETS: readonly BookFacet[] = [
  'Structure',
  'ItemText',
  'Attribution',
  'Audio',
  'Reviews',
  'NodeTitle',
];

/** Facets that change the tree or the chapter headers. */
const OVERVIEW_FACETS: readonly BookFacet[] = ['Structure', 'NodeTitle', 'Characters', 'Narrator'];

/** Facets that change which voice an item resolves to, anywhere in the book. */
const VOICE_FACETS: readonly BookFacet[] = ['Voices', 'VoiceRules', 'Characters', 'Narrator'];

/**
 * The reader's receipt → refresh decision (ticket 10, spec D6). Pure: the store feeds it what is
 * loaded and applies the plan. The client never patches rows; it only decides what to read again.
 */
export function planReceipt(receipt: Receipt, context: ReceiptContext): ReceiptPlan {
  const { effects } = receipt;
  const last = context.lastRevision;
  const gap = last !== null && receipt.revision > last + 1;
  const any = (facets: readonly BookFacet[]) => facets.some((f) => hasFacet(effects.facets, f));

  const plan: ReceiptPlan = {
    reloadChapters: [],
    reloadVoices: [],
    reloadOverview: gap,
    reloadReviews: gap,
    dropOtherChapters: gap,
    toast: false,
    gap,
  };

  const reload = new Set<string>();
  if (gap && context.visibleChapterId) reload.add(context.visibleChapterId);

  if (!effects.changedNothing) {
    if (any(CONTENT_FACETS)) {
      if (effects.scope === 'WholeProject') {
        if (context.visibleChapterId) reload.add(context.visibleChapterId);
        plan.dropOtherChapters = true;
      } else {
        for (const chapter of affectedChapters(receipt, context.loaded)) reload.add(chapter);
      }
    }
    if (any(OVERVIEW_FACETS)) plan.reloadOverview = true;
    if (hasFacet(effects.facets, 'Reviews')) plan.reloadReviews = true;
    if (any(VOICE_FACETS)) {
      plan.reloadVoices = context.loaded.map((c) => c.chapterId).filter((id) => !reload.has(id));
    }
    const structural = hasFacet(effects.facets, 'Structure') || effects.structural.length > 0;
    plan.toast = !receipt.isOwn && structural;
  }

  // Reading order, not receipt order.
  plan.reloadChapters = context.loaded.map((c) => c.chapterId).filter((id) => reload.has(id));
  return plan;
}

/** A skipped revision (or a reconnect that is ahead): re-read everything on screen. */
export function gapPlan(visibleChapterId: string | null): ReceiptPlan {
  return {
    reloadChapters: visibleChapterId ? [visibleChapterId] : [],
    reloadVoices: [],
    reloadOverview: true,
    reloadReviews: true,
    dropOtherChapters: true,
    toast: false,
    gap: true,
  };
}

function affectedChapters(receipt: Receipt, loaded: readonly LoadedChapter[]): string[] {
  const { nodeIds, paragraphIds, paragraphItemIds } = receipt.effects;
  return loaded
    .filter(
      (c) =>
        nodeIds.includes(c.chapterId) ||
        paragraphIds.some((id) => c.paragraphIds.has(id)) ||
        paragraphItemIds.some((id) => c.itemIds.has(id)),
    )
    .map((c) => c.chapterId);
}
