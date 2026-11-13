/** The `?action=` values the Book page opens: each the same as its overflow entry (spec §5). */
const ACTION_DEEP_LINK_NAMES = ['titles', 'edit-ai', 'manual-reread'] as const;
export type ActionDeepLinkName = (typeof ACTION_DEEP_LINK_NAMES)[number];

/** What the Book page reads to decide an Action deep link (`?action=`, from the Overview's rows). */
export interface ActionDeepLinkInput {
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
 * What the page does with an Action deep link now: leave the parameter for a later run, drop it, or drop
 * it and open that action's dialog. The page drops before it opens, so the link opens exactly once.
 */
export type ActionDeepLinkOutcome = 'wait' | 'drop' | { readonly open: ActionDeepLinkName };

/**
 * The `?action=` policy. It waits until the book and its Pipeline steps have loaded and nothing
 * locks the editor. A viewed History step, a book with no content or an unknown name only drops
 * the parameter, so the dialog doesn't open by itself on Back to current.
 */
export function decideActionDeepLink({
  action,
  overview,
  pipelineLoaded,
  viewing,
  blocked,
}: ActionDeepLinkInput): ActionDeepLinkOutcome {
  if (action === null || !overview || !pipelineLoaded) return 'wait';
  if (blocked && !viewing) return 'wait';
  if (viewing || !overview.hasContent || !isActionDeepLinkName(action)) return 'drop';
  return { open: action };
}

function isActionDeepLinkName(action: string): action is ActionDeepLinkName {
  return (ACTION_DEEP_LINK_NAMES as readonly string[]).includes(action);
}
