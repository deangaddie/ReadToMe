import { Injectable, computed, inject, signal } from '@angular/core';
import {
  AudioApi,
  AudioReviewsDto,
  BookApi,
  BookOverviewDto,
  ChapterVoicesDto,
  NodeChildrenDto,
  NodeDto,
  ParagraphDto,
} from '@app/api';
import { LiveSnapshot, Receipt } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subscription } from 'rxjs';
import { ProjectStore } from '../project/project-store';
import {
  ChildrenMap,
  NodeRef,
  TreeNode,
  buildTree,
  chapterSequence,
  firstUnloaded,
} from './book-tree';
import { LoadedChapter, ReceiptPlan, gapPlan, planReceipt } from './receipt-plan';
import { LoadedChapterView, ReaderMode, SpeakerContext } from './reader-rows';

/** Receipts arrive in bursts (attribution commits per paragraph); apply one merged plan per burst. */
export const RECEIPT_BATCH_MS = 150;

/** Chapters kept in the reader window; the far end is dropped past this. */
export const WINDOW_CAP = 5;

type Direction = 'next' | 'previous';

/**
 * The reader's state for one project (ticket 10, design §6.3, §9), provided by the project shell.
 * Holds the book overview, lazily loaded tree children, a window of adjacent loaded chapters with
 * their voice maps, the audio review map and the reader mode. Receipts never patch rows (spec D6):
 * {@link planReceipt} decides what to read again and the store reads it.
 */
@Injectable()
export class BookStore {
  private readonly book = inject(BookApi);
  private readonly audio = inject(AudioApi);
  private readonly live = inject(LiveService);
  private readonly project = inject(ProjectStore);
  private readonly toast = inject(ToastService);

  private subscriptions: Subscription[] = [];
  private lastRevision: number | null = null;
  private pending: ReceiptPlan | null = null;
  private batchTimer: ReturnType<typeof setTimeout> | null = null;
  /** One "updated elsewhere" toast per receipt burst. */
  private toasted = false;
  private readonly inFlight = new Set<string>();
  /** Children reads by parent id, so a second asker waits for the first instead of re-reading. */
  private readonly childLoads = new Map<string, Promise<void>>();
  /** Writers waiting for this tab's next own receipt (see {@link expectOwnReceipt}). */
  private readonly ownWaiters = new Set<(hit: boolean) => void>();

  private readonly _folder = signal<string | null>(null);
  private readonly _overview = signal<BookOverviewDto | null>(null);
  private readonly _children = signal<Record<string, NodeDto[]>>({});
  private readonly _paragraphs = signal<Record<string, ParagraphDto[]>>({});
  private readonly _voices = signal<Record<string, ChapterVoicesDto>>({});
  private readonly _reviews = signal<AudioReviewsDto>({});
  private readonly _window = signal<string[]>([]);
  private readonly _current = signal<string | null>(null);
  private readonly _mode = signal<ReaderMode>('read');
  private readonly _expanded = signal<ReadonlySet<string>>(new Set());
  private readonly _stale = signal<string | null>(null);
  private readonly _scrollRequest = signal<{ chapterId: string; seq: number } | null>(null);

  readonly folder = this._folder.asReadonly();
  readonly overview = this._overview.asReadonly();
  readonly reviews = this._reviews.asReadonly();
  readonly window = this._window.asReadonly();
  readonly currentChapterId = this._current.asReadonly();
  readonly mode = this._mode.asReadonly();
  /** Tree nodes the reader has expanded; kept here so the tree reopens as it was left. */
  readonly expanded = this._expanded.asReadonly();
  /** Why the view may be out of date (a reload failed); null when current. */
  readonly stale = this._stale.asReadonly();
  /** Set when the reader should scroll a chapter header to the top; `seq` re-fires the same chapter. */
  readonly scrollRequest = this._scrollRequest.asReadonly();

  readonly tree = computed(() => buildTree(this._overview()?.volumes ?? [], this._children()));

  readonly sequence = computed(() =>
    chapterSequence(this._overview()?.volumes ?? [], this._children()),
  );

  /** The loaded window, in reading order, as the reader renders it. */
  readonly chapters = computed<LoadedChapterView[]>(() => {
    const titles = new Map(this.sequence().map((c) => [c.id, c.title]));
    const paragraphs = this._paragraphs();
    return this._window()
      .filter((id) => paragraphs[id])
      .map((id) => ({ id, title: titles.get(id) ?? 'Chapter', paragraphs: paragraphs[id]! }));
  });

  readonly speakers = computed<SpeakerContext>(() => ({
    names: Object.fromEntries((this._overview()?.characters ?? []).map((c) => [c.id, c.name])),
    narrator: this.project.detail()?.narrator ?? null,
  }));

  /** Voice per item across the loaded window. */
  readonly itemVoices = computed<ChapterVoicesDto>(() =>
    Object.assign({}, ...this._window().map((id) => this._voices()[id] ?? {})),
  );

  // ---- lifecycle --------------------------------------------------------------------------------

  /** Loads the overview and reviews and starts listening for receipts. No-op when already open. */
  async open(folder: string): Promise<void> {
    if (this._folder() === folder) return;
    this.close();
    this._folder.set(folder);
    this.lastRevision = this.baselineRevision();
    this.subscriptions = [
      this.live.receipts$(folder).subscribe((r) => this.onReceipt(r)),
      this.live.resynced$.subscribe((s) => this.onResync(s)),
    ];

    try {
      await Promise.all([this.loadOverview(), this.loadReviews()]);
      this._stale.set(null);
    } catch (error) {
      if (this._folder() === folder) this._stale.set(messageOf(error));
    }
  }

  close(): void {
    for (const s of this.subscriptions) s.unsubscribe();
    this.subscriptions = [];
    if (this.batchTimer) clearTimeout(this.batchTimer);
    this.batchTimer = null;
    this.pending = null;
    this.inFlight.clear();
    this.childLoads.clear();
    this.settleOwnWaiters(false);
    this._folder.set(null);
    this._overview.set(null);
    this._children.set({});
    this._paragraphs.set({});
    this._voices.set({});
    this._reviews.set({});
    this._window.set([]);
    this._current.set(null);
    this._expanded.set(new Set());
    this._stale.set(null);
  }

  setMode(mode: ReaderMode): void {
    if (this._mode() === mode) return;
    this._mode.set(mode);
    if (mode === 'audio') {
      for (const id of this._window()) {
        if (!this._voices()[id]) void this.readOrMarkStale(() => this.loadVoices(id));
      }
    }
  }

  /** The chapter at the top of the viewport, reported by the reader as it scrolls. */
  setCurrentChapter(chapterId: string): void {
    this._current.set(chapterId);
  }

  // ---- tree -------------------------------------------------------------------------------------

  /** The tree expanded or collapsed a node; an expanded node's children load if they are missing. */
  setExpanded(node: TreeNode, expanded: boolean): void {
    if (expanded === this._expanded().has(node.id)) return;
    this._expanded.update((set) => {
      const next = new Set(set);
      if (expanded) next.add(node.id);
      else next.delete(node.id);
      return next;
    });
    if (expanded && !node.loaded) {
      void this.readOrMarkStale(() => this.loadChildren(node.loadTarget));
    }
  }

  /**
   * Loads a volume's parts or a part's chapters; a single part is followed through to its chapters.
   * A read already under way for the same parent is shared, not skipped: a caller that loops until
   * the children are known (see {@link findChapter}) must actually wait for them.
   */
  private loadChildren(ref: NodeRef): Promise<void> {
    const folder = this._folder();
    if (!folder || ref.level === 'chapter' || this._children()[ref.id]) return Promise.resolve();
    const existing = this.childLoads.get(ref.id);
    if (existing) return existing;

    const load = (async () => {
      try {
        const result = await this.book.children(folder, ref.level, ref.id);
        if (this._folder() !== folder) return;
        const nodes = childNodes(ref, result);
        this._children.update((c) => ({ ...c, [ref.id]: nodes }));
        const [only] = nodes;
        if (ref.level === 'volume' && only && nodes.length === 1) {
          await this.loadChildren({ level: 'part', id: only.id });
        }
      } finally {
        this.childLoads.delete(ref.id);
      }
    })();
    this.childLoads.set(ref.id, load);
    return load;
  }

  // ---- reader window ----------------------------------------------------------------------------

  /** Shows the first chapter of the book (loading structure until one is known). */
  async openFirstChapter(): Promise<void> {
    const first = await this.findChapter((seq) => seq[0]?.id);
    if (first) await this.openChapter(first);
  }

  /** Scrolls to a chapter, replacing the window unless it is already loaded. */
  async openChapter(chapterId: string): Promise<void> {
    if (!this._window().includes(chapterId)) {
      this._window.set([chapterId]);
      await this.readOrMarkStale(() => this.loadChapter(chapterId));
    }
    this._current.set(chapterId);
    this._scrollRequest.update((r) => ({ chapterId, seq: (r?.seq ?? 0) + 1 }));
  }

  /** Appends the chapter after the window, if there is one. */
  loadNext(): Promise<void> {
    return this.extend('next');
  }

  /** Prepends the chapter before the window, if there is one. */
  loadPrevious(): Promise<void> {
    return this.extend('previous');
  }

  /** The stale banner's Refresh: read everything on screen again. */
  async refresh(): Promise<void> {
    this._stale.set(null);
    try {
      await Promise.all([
        this.reloadOverview(),
        this.loadReviews(),
        ...this._window().map((id) => this.loadChapter(id)),
      ]);
      await this.reconcileWindow();
    } catch (error) {
      this._stale.set(messageOf(error));
    }
  }

  private async extend(direction: Direction): Promise<void> {
    const window = this._window();
    const edge = direction === 'next' ? window.at(-1) : window[0];
    if (!edge) return;
    const key = `extend:${direction}`;
    if (this.inFlight.has(key)) return;
    this.inFlight.add(key);
    try {
      const target = await this.findChapter((seq) => {
        const i = seq.findIndex((c) => c.id === edge);
        if (i < 0) return undefined;
        const j = direction === 'next' ? i + 1 : i - 1;
        // Before the first chapter there is nothing; past the last loaded one, load more.
        return j < 0 ? null : seq[j]?.id;
      });
      if (!target || this._window().includes(target)) return;
      await this.loadChapter(target);
      this._window.update((w) => {
        const next = direction === 'next' ? [...w, target] : [target, ...w];
        if (next.length <= WINDOW_CAP) return next;
        return direction === 'next' ? next.slice(1) : next.slice(0, WINDOW_CAP);
      });
    } catch (error) {
      this._stale.set(messageOf(error));
    } finally {
      this.inFlight.delete(key);
    }
  }

  /**
   * Resolves a chapter id from the chapter sequence, loading structure until `pick` answers:
   * `undefined` = not known yet (load more), `null` = there is none.
   */
  private async findChapter(
    pick: (seq: ReturnType<typeof chapterSequence>) => string | null | undefined,
  ): Promise<string | null> {
    for (;;) {
      const found = pick(this.sequence());
      if (found !== undefined) return found;
      const missing = firstUnloaded(this._overview()?.volumes ?? [], this._children());
      if (!missing) return null;
      await this.loadChildren(missing);
      // A read that left the node unloaded (the store closed or moved on) must not be asked again
      // in a loop that would never end.
      if (!this._children()[missing.id]) return null;
    }
  }

  // ---- reads ------------------------------------------------------------------------------------

  private async loadOverview(): Promise<void> {
    const folder = this._folder()!;
    const overview = await this.book.overview(folder);
    if (this._folder() !== folder) return;
    this._overview.set(overview);
    this.lastRevision ??= this.baselineRevision();
    const [only] = overview.volumes;
    if (only && overview.volumes.length === 1)
      await this.loadChildren({ level: 'volume', id: only.id });
  }

  /** Overview plus every tree level already loaded, so the tree keeps its shape. */
  private async reloadOverview(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const overview = await this.book.overview(folder);
    const loaded = this._children();
    const refs = childRefs(overview.volumes, loaded);
    const results = await Promise.all(
      refs.map((ref) => this.book.children(folder, ref.level, ref.id)),
    );
    if (this._folder() !== folder) return;
    const children: Record<string, NodeDto[]> = {};
    refs.forEach((ref, i) => {
      children[ref.id] = childNodes(ref, results[i]!);
    });
    this._overview.set(overview);
    this._children.set(children);
  }

  private async loadReviews(): Promise<void> {
    const folder = this._folder()!;
    const reviews = await this.audio.reviews(folder);
    if (this._folder() === folder) this._reviews.set(reviews);
  }

  private async loadChapter(chapterId: string): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const [children] = await Promise.all([
      this.book.children(folder, 'chapter', chapterId),
      this._mode() === 'audio' ? this.loadVoices(chapterId) : Promise.resolve(),
    ]);
    if (this._folder() !== folder) return;
    this._paragraphs.update((p) => ({ ...p, [chapterId]: children.paragraphs ?? [] }));
    if (this._mode() !== 'audio') this.forgetVoices([chapterId]);
  }

  private async loadVoices(chapterId: string): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const voices = await this.book.chapterVoices(folder, chapterId);
    if (this._folder() === folder) this._voices.update((v) => ({ ...v, [chapterId]: voices }));
  }

  private forgetVoices(chapterIds: string[]): void {
    this._voices.update((v) => {
      const next = { ...v };
      for (const id of chapterIds) delete next[id];
      return next;
    });
  }

  /**
   * After the structure changed: drop window chapters that no longer exist and fill any chapter that
   * appeared between the first and last kept ones, so the window stays a contiguous run of the book.
   */
  private async reconcileWindow(): Promise<void> {
    const sequence = this.sequence().map((c) => c.id);
    const kept = this._window().filter((id) => sequence.includes(id));
    if (kept.length === 0) {
      this._window.set([]);
      if (this._overview()?.hasContent) await this.openFirstChapter();
      return;
    }
    const from = sequence.indexOf(kept[0]!);
    const to = sequence.indexOf(kept.at(-1)!);
    const window = sequence.slice(from, to + 1).slice(0, WINDOW_CAP);
    const paragraphs = this._paragraphs();
    await Promise.all(window.filter((id) => !paragraphs[id]).map((id) => this.loadChapter(id)));
    const current = this._current();
    if (current && !window.includes(current)) this._current.set(window[0]!);
    this._window.set(window);
  }

  /** The project revision once `/status` has answered (0 is a real revision); null before. */
  private baselineRevision(): number | null {
    return this.project.status() ? this.project.revision() : null;
  }

  /** Runs a read; a failure marks the view stale instead of throwing into the caller. */
  private async readOrMarkStale(read: () => Promise<void>): Promise<void> {
    try {
      await read();
    } catch (error) {
      this._stale.set(messageOf(error));
    }
  }

  // ---- receipts ---------------------------------------------------------------------------------

  /**
   * A writer's handle on its own commit: `settled` resolves true when the next receipt stamped with
   * this tab's origin arrives (the reload it plans is already scheduled), false after `timeoutMs`,
   * on `cancel()` (the request failed, nothing is coming) or when the store closes.
   */
  expectOwnReceipt(timeoutMs: number): { settled: Promise<boolean>; cancel(): void } {
    let resolve!: (hit: boolean) => void;
    const settled = new Promise<boolean>((r) => (resolve = r));
    const timer = setTimeout(() => finish(false), timeoutMs);
    const finish = (hit: boolean) => {
      clearTimeout(timer);
      this.ownWaiters.delete(finish);
      resolve(hit);
    };
    this.ownWaiters.add(finish);
    return { settled, cancel: () => finish(false) };
  }

  private settleOwnWaiters(hit: boolean): void {
    for (const waiter of [...this.ownWaiters]) waiter(hit);
  }

  private loadedIndex(): LoadedChapter[] {
    const paragraphs = this._paragraphs();
    return this._window()
      .filter((id) => paragraphs[id])
      .map((id) => ({
        chapterId: id,
        paragraphIds: new Set(paragraphs[id]!.map((p) => p.id)),
        itemIds: new Set(paragraphs[id]!.flatMap((p) => p.items.map((i) => i.id))),
      }));
  }

  private onReceipt(receipt: Receipt): void {
    const plan = planReceipt(receipt, {
      lastRevision: this.lastRevision,
      loaded: this.loadedIndex(),
      visibleChapterId: this._current(),
    });
    if (this.lastRevision === null || receipt.revision > this.lastRevision) {
      this.lastRevision = receipt.revision;
    }
    if (plan.toast) this.toastOnce();
    this.schedule(plan);
    if (receipt.isOwn) this.settleOwnWaiters(true);
  }

  private onResync(snapshot: LiveSnapshot): void {
    const folder = this._folder();
    if (!folder) return;
    const key = Object.keys(snapshot.projects ?? {}).find(
      (k) => k.toLowerCase() === folder.toLowerCase(),
    );
    const revision = key === undefined ? undefined : snapshot.projects[key]?.revision;
    if (revision === undefined || (this.lastRevision !== null && revision <= this.lastRevision))
      return;
    this.lastRevision = revision;
    this.schedule(gapPlan(this._current()));
  }

  private toastOnce(): void {
    if (this.toasted) return;
    this.toasted = true;
    this.toast.info('Book updated elsewhere');
  }

  private schedule(plan: ReceiptPlan): void {
    this.pending = this.pending ? mergePlans(this.pending, plan) : plan;
    if (this.batchTimer) return;
    this.batchTimer = setTimeout(() => {
      this.batchTimer = null;
      const next = this.pending;
      this.pending = null;
      this.toasted = false;
      if (next) void this.apply(next);
    }, RECEIPT_BATCH_MS);
  }

  private async apply(plan: ReceiptPlan): Promise<void> {
    if (plan.dropOtherChapters) {
      const keep = new Set(plan.reloadChapters);
      const current = this._current();
      if (current) keep.add(current);
      this._window.update((w) => w.filter((id) => keep.has(id)));
    }
    const window = new Set(this._window());
    const chapters = plan.reloadChapters.filter((id) => window.has(id));
    const voices = this._mode() === 'audio' ? plan.reloadVoices.filter((id) => window.has(id)) : [];
    if (this._mode() !== 'audio') this.forgetVoices(plan.reloadVoices);

    try {
      await Promise.all([
        plan.reloadOverview ? this.reloadOverview() : Promise.resolve(),
        plan.reloadReviews ? this.loadReviews() : Promise.resolve(),
        ...chapters.map((id) => this.loadChapter(id)),
        ...voices.map((id) => this.loadVoices(id)),
      ]);
      if (plan.reloadOverview) await this.reconcileWindow();
    } catch (error) {
      this._stale.set(messageOf(error));
    }
  }
}

function mergePlans(a: ReceiptPlan, b: ReceiptPlan): ReceiptPlan {
  const union = (x: string[], y: string[]) => [...new Set([...x, ...y])];
  return {
    reloadChapters: union(a.reloadChapters, b.reloadChapters),
    reloadVoices: union(a.reloadVoices, b.reloadVoices),
    reloadOverview: a.reloadOverview || b.reloadOverview,
    reloadReviews: a.reloadReviews || b.reloadReviews,
    dropOtherChapters: a.dropOtherChapters || b.dropOtherChapters,
    toast: a.toast || b.toast,
    gap: a.gap || b.gap,
  };
}

/** The list a volume (parts) or part (chapters) children read answers with. */
function childNodes(ref: NodeRef, result: NodeChildrenDto): NodeDto[] {
  return (ref.level === 'volume' ? result.parts : result.chapters) ?? [];
}

/** Volumes/parts whose children were loaded, still present in the fresh overview. */
function childRefs(volumes: readonly NodeDto[], loaded: ChildrenMap): NodeRef[] {
  const refs: NodeRef[] = [];
  for (const volume of volumes) {
    const parts = loaded[volume.id];
    if (!parts) continue;
    refs.push({ level: 'volume', id: volume.id });
    for (const part of parts) if (loaded[part.id]) refs.push({ level: 'part', id: part.id });
  }
  return refs;
}

function messageOf(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}
