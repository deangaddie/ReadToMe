/** The `?action=` values the Book page opens: each the same as its overflow entry (spec §5). */
const DEEP_LINK_ACTIONS = ['titles', 'edit-ai', 'manual-reread'] as const;
export type DeepLinkAction = (typeof DEEP_LINK_ACTIONS)[number];

/** What the Book page reads to decide a `?action=` deep link (from the Overview's rows). */
export interface DeepLinkInput {
  /** The `?action=` value, or null when the URL has none. */
  readonly action: string | null;
  /** The book's overview, or null until it has loaded. */
  readonly overview: { readonly hasContent: boolean } | null;
  /** Whether the project's Pipeline steps have loaded: the dialog shows their advice. */
  readonly pipelineLoaded: boolean;
  /** Whether a History step is viewed. */
  readonly viewing: boolean;
  /** Whether something locks the editor. */
  readonly blocked: boolean;
}

/**
 * What the page does with a deep link now: leave the parameter for a later run, drop it, or drop
 * it and open that action's dialog. The page drops before it opens, so a link opens exactly once.
 */
export type DeepLinkStep = 'wait' | 'drop' | { readonly open: DeepLinkAction };

/**
 * The `?action=` policy. It waits until the book and its Pipeline steps have loaded and nothing
 * locks the editor. A viewed History step, a book with no content or an unknown name only drops
 * the parameter, so the dialog doesn't open by itself on Back to current.
 */
export function deepLinkStep({
  action,
  overview,
  pipelineLoaded,
  viewing,
  blocked,
}: DeepLinkInput): DeepLinkStep {
  if (action === null || !overview || !pipelineLoaded) return 'wait';
  if (blocked && !viewing) return 'wait';
  if (viewing || !overview.hasContent || !isDeepLinkAction(action)) return 'drop';
  return { open: action };
}

function isDeepLinkAction(action: string): action is DeepLinkAction {
  return (DEEP_LINK_ACTIONS as readonly string[]).includes(action);
}
