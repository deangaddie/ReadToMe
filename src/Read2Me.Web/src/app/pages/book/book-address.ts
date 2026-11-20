import { READER_MODES, type ReaderMode } from './reader-rows';

/**
 * The Book address: the Book page's place in the URL (`?mode=`, `?chapter=`, `?action=`). Every
 * Book link is built here and the page reads its query back here, so a producer's names and the
 * page's names are the same names. The default Reader mode, `read`, is written as no mode, so
 * each Book address has exactly one link.
 */

/** The `?action=` values the Book page opens: each the same as its overflow entry (spec §5). */
export const ACTION_DEEP_LINK_NAMES = ['titles', 'edit-ai', 'manual-reread'] as const;
export type ActionDeepLinkName = (typeof ACTION_DEEP_LINK_NAMES)[number];

/** A Book address to link to; a key left out is absent from the link. */
export interface BookAddress {
  readonly mode?: ReaderMode;
  readonly chapter?: string;
  readonly action?: ActionDeepLinkName;
}

/** A Book address as the page reads it from its query. */
export interface ParsedBookAddress {
  readonly mode: ReaderMode;
  /** The open chapter's id, unvalidated; null when the URL names none. */
  readonly chapter: string | null;
  /** The Action deep link: null when absent, `unknown` for a name the page doesn't know. */
  readonly action: ActionDeepLinkName | 'unknown' | null;
}

/** One change to the current Book address, for {@link Router.replaceQuery}. */
export type BookQueryPatch =
  | { readonly mode: ReaderMode }
  | { readonly chapter: string | null }
  | { readonly action: null };

/** The app-relative Book link for `address`, e.g. `projects/x/book?mode=speakers&chapter=c1`. */
export function bookLink(folder: string, address: BookAddress = {}): string {
  const query = new URLSearchParams();
  if (address.mode && address.mode !== 'read') query.set('mode', address.mode);
  if (address.chapter !== undefined) query.set('chapter', address.chapter);
  if (address.action) query.set('action', address.action);
  const search = query.toString();
  return `projects/${encodeURIComponent(folder)}/book${search ? `?${search}` : ''}`;
}

export function parseBookAddress(query: URLSearchParams): ParsedBookAddress {
  const action = query.get('action');
  return {
    mode: READER_MODES.find((m) => m === query.get('mode')) ?? 'read',
    chapter: query.get('chapter'),
    action:
      action === null ? null : (ACTION_DEEP_LINK_NAMES.find((a) => a === action) ?? 'unknown'),
  };
}

/** The query keys `patch` sets or deletes; the default Reader mode deletes `mode`. */
export function bookQuery(patch: BookQueryPatch): Record<string, string | null> {
  if ('mode' in patch) return { mode: patch.mode === 'read' ? null : patch.mode };
  return { ...patch };
}
