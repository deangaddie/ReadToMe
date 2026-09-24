import { Injectable, computed, inject, signal } from '@angular/core';
import { ProjectDetailDto, ProjectStatusDto, ProjectsApi } from '@app/api';
import {
  BookFacet,
  ItemStatusEntry,
  ItemStatusMessage,
  LiveSnapshot,
  NodeStatusMessage,
  NodeStatusSummary,
  ParagraphStatusEntry,
  Receipt,
  hasFacet,
} from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ProjectTitles } from '@app/shell/project-titles';
import { Subscription } from 'rxjs';
import { Debounced, REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';

export { REFETCH_DEBOUNCE_MS };

/** Facets that move a `/status` count: structure, speakers, audio, reviews, cast and voices. */
const STATUS_FACETS: readonly BookFacet[] = [
  'Structure',
  'Attribution',
  'Audio',
  'Reviews',
  'Characters',
  'Narrator',
  'Voices',
];

/** Facets the project detail shows: narrator link and name, narrator-only policy. */
const DETAIL_FACETS: readonly BookFacet[] = ['Characters', 'Narrator', 'ProjectPolicy'];

type StatusMap<T> = Record<string, T | null>;

/**
 * One project's state for every route under `/projects/{folder}` (ticket 09, design §9), provided
 * by the project shell. Holds the detail and the `/status` bootstrap, keeps the node and item
 * status maps current from hub deltas, and turns receipts into refetches — never into entity
 * patches (spec D6): a receipt whose facets move a count, or whose revision skips one, refetches
 * `/status`; one that touches the narrator or project policy refetches the detail.
 */
@Injectable()
export class ProjectStore {
  private readonly api = inject(ProjectsApi);
  private readonly live = inject(LiveService);
  private readonly titles = inject(ProjectTitles);

  private subscriptions: Subscription[] = [];
  private readonly statusRefetch = new Debounced(() => this.refreshStatus());
  private readonly detailRefetch = new Debounced(() => this.refreshDetail());

  private readonly _folder = signal<string | null>(null);
  private readonly _detail = signal<ProjectDetailDto | null>(null);
  private readonly _status = signal<ProjectStatusDto | null>(null);
  private readonly _revision = signal(0);
  private readonly _nodes = signal<StatusMap<NodeStatusSummary>>({});
  private readonly _folderAudioRemaining = signal(0);
  private readonly _paragraphs = signal<StatusMap<ParagraphStatusEntry>>({});
  private readonly _items = signal<StatusMap<ItemStatusEntry>>({});
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);

  readonly folder = this._folder.asReadonly();
  readonly detail = this._detail.asReadonly();
  /** The last `/status` read; its `nodes` and `audio.remaining` go stale — read {@link nodes} instead. */
  readonly status = this._status.asReadonly();
  /** Highest book revision seen, from `/status` or a receipt. */
  readonly revision = this._revision.asReadonly();
  readonly nodes = this._nodes.asReadonly();
  readonly folderAudioRemaining = this._folderAudioRemaining.asReadonly();
  readonly paragraphs = this._paragraphs.asReadonly();
  readonly items = this._items.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();

  /** Items the audio queue reports queued or processing for this project. */
  readonly audioInFlight = computed(
    () =>
      Object.values(this._items()).filter(
        (i) => i?.status === 'Queued' || i?.status === 'Processing',
      ).length,
  );

  /** Joins the project group, listens to its hub traffic and loads detail + status. */
  async open(folder: string): Promise<void> {
    this.close();
    this._folder.set(folder);
    this._detail.set(null);
    this._status.set(null);
    this._error.set(null);

    this.subscriptions = [
      this.live.on('nodeStatus').subscribe((m) => this.applyNodeStatus(m)),
      this.live.on('itemStatus').subscribe((m) => this.applyItemStatus(m)),
      this.live.receipts$(folder).subscribe((r) => this.onReceipt(r)),
      this.live.resynced$.subscribe((s) => this.onResync(s)),
    ];
    void this.live.joinProject(folder).then(() => this.seedItemsFromLive());

    this._loading.set(true);
    try {
      const [detail, status] = await Promise.allSettled([
        this.api.get(folder),
        this.api.status(folder),
      ]);
      if (this._folder() !== folder) return;
      if (detail.status === 'fulfilled') this.setDetail(detail.value);
      if (status.status === 'fulfilled') this.applyStatus(status.value);
      const failure = [detail, status].find((r) => r.status === 'rejected');
      if (failure) this._error.set(messageOf(failure.reason));
    } finally {
      this._loading.set(false);
    }
  }

  /** Leaves the group and drops pending refetches. Safe to call when nothing is open. */
  close(): void {
    for (const s of this.subscriptions) s.unsubscribe();
    this.subscriptions = [];
    this.clearTimers();
    const folder = this._folder();
    if (folder) void this.live.leaveProject(folder);
    this._folder.set(null);
  }

  /** After this client's own PATCH: the response is the new detail. */
  setDetail(detail: ProjectDetailDto): void {
    this._detail.set(detail);
    this.titles.set(detail.folderName, detail.title);
  }

  async refreshStatus(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const status = await this.api.status(folder);
    if (this._folder() === folder) this.applyStatus(status);
  }

  async refreshDetail(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    const detail = await this.api.get(folder);
    if (this._folder() === folder) this.setDetail(detail);
  }

  private applyStatus(status: ProjectStatusDto): void {
    this._status.set(status);
    this._revision.set(status.revision);
    this._nodes.set(status.nodes);
    this._folderAudioRemaining.set(status.audio.remaining);
  }

  private applyNodeStatus(m: NodeStatusMessage): void {
    if (!this.isMine(m.folder)) return;
    this._nodes.update((nodes) => mergeDeltas(nodes, m.nodes));
    this._folderAudioRemaining.set(m.folderAudioRemaining);
  }

  private applyItemStatus(m: ItemStatusMessage): void {
    if (!this.isMine(m.folder)) return;
    this._paragraphs.update((p) => mergeDeltas(p, m.paragraphs));
    this._items.update((i) => mergeDeltas(i, m.items));
  }

  private onReceipt(r: Receipt): void {
    const seen = this._revision();
    const gap = r.revision > seen + 1;
    if (r.revision > seen) this._revision.set(r.revision);

    const facets = r.effects.facets;
    if (gap || STATUS_FACETS.some((f) => hasFacet(facets, f))) this.scheduleStatus();
    if (DETAIL_FACETS.some((f) => hasFacet(facets, f))) this.scheduleDetail();
  }

  /** After a reconnect the hub may have committed revisions this client never heard about. */
  private onResync(snapshot: LiveSnapshot): void {
    const project = this.projectIn(snapshot.projects);
    if (!project) return;
    this.seedItems(project.paragraphs, project.items);
    if (project.revision > this._revision()) this.scheduleStatus();
  }

  private seedItemsFromLive(): void {
    const project = this.projectIn(this.live.projects());
    if (project) this.seedItems(project.paragraphs, project.items);
  }

  private seedItems(
    paragraphs: StatusMap<ParagraphStatusEntry> | undefined,
    items: StatusMap<ItemStatusEntry> | undefined,
  ): void {
    if (paragraphs) this._paragraphs.set(paragraphs);
    if (items) this._items.set(items);
  }

  private projectIn<T>(projects: Record<string, T> | undefined): T | undefined {
    const folder = this._folder();
    if (!folder || !projects) return undefined;
    const key = Object.keys(projects).find((k) => sameFolder(k, folder));
    return key === undefined ? undefined : projects[key];
  }

  private scheduleStatus(): void {
    this.statusRefetch.schedule();
  }

  private scheduleDetail(): void {
    this.detailRefetch.schedule();
  }

  private clearTimers(): void {
    this.statusRefetch.cancel();
    this.detailRefetch.cancel();
  }

  private isMine(folder: string): boolean {
    const mine = this._folder();
    return mine !== null && sameFolder(mine, folder);
  }
}

/** The hub compares folders case-insensitively. */
function sameFolder(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

function mergeDeltas<T>(current: StatusMap<T>, deltas: StatusMap<T>): StatusMap<T> {
  const next = { ...current };
  for (const [key, value] of Object.entries(deltas)) {
    if (value === null) delete next[key];
    else next[key] = value;
  }
  return next;
}

function messageOf(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}
