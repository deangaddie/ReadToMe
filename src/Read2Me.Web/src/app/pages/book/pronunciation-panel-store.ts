import { type Guid, type PronunciationDto, PronunciationsApi } from '@app/api';
import { ActivityStore } from '@app/activity/activity-store';
import { use } from '@app/core/services';
import { computed, effect, signal, untracked } from '@app/core/signals';
import { hasFacet } from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { Debounced } from '@app/shared/debounced';
import { confirmDiscard } from '@app/shared/unsaved-changes';
import { ConfirmService } from '@app/ui/dialogs';
import type { Forms } from '../pronunciations/pronunciation-logic';
import type { TextPlace } from '../pronunciations/try-out-logic';

/** What the panel's editor holds: an entry by id, or a new one pre-filled with `forms`. */
export interface PanelSession {
  /** Bumped per session: a new session is a fresh editor. */
  key: number;
  entryId: Guid | null;
  /** A new entry's first draft; null for an entry, which opens with its own forms. */
  forms: Forms | null;
  /** Where in the book it was opened from: the sentence its try-out plays. */
  origin: TextPlace | null;
}

/**
 * The Book view's Pronunciations panel state (pronunciation ticket 16), owned by the page: whether
 * the panel is open, what its editor holds, the row the pointer is on, and the book's list (the
 * editor's entries and the pill's Edit test), reread on every receipt with the `Pronunciations`
 * facet. The panel and the Activity drawer share the right-hand space: opening one closes the
 * other. Closing the panel keeps its session, so a draft is there when it opens again; `isDirty`
 * says whether that draft has unsaved changes, and another session asks before replacing it.
 */
export class PronunciationPanelState {
  private readonly api = use(PronunciationsApi);
  private readonly live = use(LiveService);
  private readonly activity = use(ActivityStore);
  private readonly confirm = use(ConfirmService);

  readonly open = signal(false);
  readonly session = signal<PanelSession | null>(null);
  /** The panel row the pointer is on. */
  readonly hovered = signal<Guid | null>(null);
  readonly entries = signal<PronunciationDto[]>([]);

  /** The entries whose marks light: the hovered row's and the one the editor holds. */
  readonly lit = computed<ReadonlySet<Guid>>(() => {
    const lit = new Set<Guid>();
    const hovered = this.hovered();
    const editing = this.session()?.entryId;
    if (hovered) lit.add(hovered);
    if (editing) lit.add(editing);
    return lit;
  });

  private folder: string | null = null;
  private unsubscribe: Unsubscribe | null = null;
  private keys = 0;
  private seq = 0;
  private readonly refetch = new Debounced(() => this.load());
  private readonly stopDrawerWatch: () => void;

  constructor(private readonly isDirty: () => boolean) {
    this.stopDrawerWatch = effect(() => {
      if (this.activity.drawerOpen()) untracked(() => this.open.set(false));
    });
  }

  /** The page shows `folder`: read its list, and again on every list change. */
  follow(folder: string): void {
    if (folder === this.folder) return;
    this.unfollow();
    this.folder = folder;
    this.unsubscribe = this.live.receipts(folder, (r) => {
      if (hasFacet(r.effects.facets, 'Pronunciations')) this.refetch.schedule();
    });
    this.refetch.schedule();
  }

  dispose(): void {
    this.unfollow();
    this.stopDrawerWatch();
  }

  private unfollow(): void {
    this.unsubscribe?.();
    this.unsubscribe = null;
    this.refetch.cancel();
    this.folder = null;
    this.seq++;
    this.entries.set([]);
    this.session.set(null);
  }

  show(): void {
    this.activity.closeDrawer();
    this.open.set(true);
  }

  hide(): void {
    this.open.set(false);
    this.hovered.set(null);
  }

  toggle(): void {
    if (this.open()) this.hide();
    else this.show();
  }

  /** Opens the panel's editor on an entry; answers whether it did (an unsaved draft may stay). */
  edit(entryId: Guid, origin: TextPlace | null): Promise<boolean> {
    return this.begin({ entryId, forms: null, origin });
  }

  /** Opens the panel's editor on a new entry pre-filled with `forms`. */
  add(forms: Forms, origin: TextPlace | null): Promise<boolean> {
    return this.begin({ entryId: null, forms, origin });
  }

  /** The editor closed: saved, deleted or cancelled. */
  endSession(): void {
    this.session.set(null);
  }

  private async begin(next: Omit<PanelSession, 'key'>): Promise<boolean> {
    const current = this.session();
    const same = current !== null && next.entryId !== null && current.entryId === next.entryId;
    if (!same && current !== null && this.isDirty() && !(await confirmDiscard(this.confirm))) {
      this.show();
      return false;
    }
    this.session.set(
      same ? { ...current, origin: next.origin ?? current.origin } : { key: ++this.keys, ...next },
    );
    this.show();
    return true;
  }

  private async load(): Promise<void> {
    const folder = this.folder;
    if (folder === null) return;
    const seq = ++this.seq;
    try {
      const entries = await this.api.list(folder);
      if (seq !== this.seq) return;
      this.entries.set(entries);
      // An entry deleted elsewhere leaves nothing to edit.
      const entryId = this.session()?.entryId;
      if (entryId && !entries.some((e) => e.id === entryId)) this.session.set(null);
    } catch {
      // The list stays as last read; the next receipt reads it again.
    }
  }
}
