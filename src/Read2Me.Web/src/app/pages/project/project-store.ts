import {
  AssemblyApi,
  type AssemblyOutputDto,
  type ProjectDetailDto,
  type ProjectStatusDto,
  ProjectsApi,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import {
  type BookFacet,
  type ItemStatusEntry,
  type ItemStatusMessage,
  type LiveSnapshot,
  type NodeStatusMessage,
  type NodeStatusSummary,
  type ParagraphStatusEntry,
  type Receipt,
  hasFacet,
} from '@app/live/live-messages';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { ProjectTitles } from '@app/shell/project-titles';
import { Debounced, REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { type PipelineStep, derivePipeline } from './pipeline-steps';

export { REFETCH_DEBOUNCE_MS };

/** Facets that move a `/status` value: structure, speakers, audio, reviews, cast, voices and checkpoints. */
const STATUS_FACETS: readonly BookFacet[] = [
  'Structure',
  'Attribution',
  'Audio',
  'Reviews',
  'Characters',
  'Narrator',
  'Voices',
  'Checkpoints',
];

/** Facets the project detail shows: narrator link and name, narrator-only policy. */
const DETAIL_FACETS: readonly BookFacet[] = ['Characters', 'Narrator', 'ProjectPolicy'];

type StatusMap<T> = Record<string, T | null>;

/**
 * One project's state for every route under `/projects/{folder}` (design §9), provided by the
 * project shell. Holds the detail and the `/status` bootstrap, keeps the node and item status maps
 * current from hub deltas, and turns receipts into refetches — never into entity patches (spec D6):
 * a receipt whose facets move a count, or whose revision skips one, refetches `/status`; one that
 * touches the narrator or project policy refetches the detail. It also owns the nine-step
 * {@link pipeline} and its last input: the newest build, read on open and again when this
 * project's assembly completes, plus the hub's voice-batch and assembly flags.
 */
export class ProjectStore {
  private readonly api = use(ProjectsApi);
  private readonly assembly = use(AssemblyApi);
  private readonly live = use(LiveService);
  private readonly titles = use(ProjectTitles);

  private subscriptions: Unsubscribe[] = [];
  /** Bumped per detail + status read, so a read overtaken by a newer one (at another step) lands nowhere. */
  private loads = 0;
  private readonly statusRefetch = new Debounced(() => this.refreshStatus());
  private readonly detailRefetch = new Debounced(() => this.refreshDetail());
  /** The highest snapshot revision that arrived before `/status` answered, compared once it does. */
  private heldSnapshot: number | null = null;

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
  /** Newest assembled audiobook, for the Export step; null when none or the read failed. */
  private readonly _lastOutput = signal<AssemblyOutputDto | null>(null);

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

  /** The nine Pipeline steps (spec §2); null until `/status` has answered. */
  readonly pipeline = computed<PipelineStep[] | null>(() => {
    const status = this._status();
    if (!status) return null;
    const output = this._lastOutput();
    return derivePipeline({
      status,
      nodes: this._nodes(),
      folderAudioRemaining: this._folderAudioRemaining(),
      audioInFlight: this.audioInFlight(),
      voiceBatchRunning: this.live.voiceBatch().isRunning,
      assemblyRunning: this.live.assembly().isRunning,
      lastBuild: output
        ? { date: formatBuildDate(output.createdAt), isPartial: output.isPartial }
        : null,
    });
  });

  /** Joins the project group, listens to its hub traffic and loads detail + status. */
  async open(folder: string): Promise<void> {
    this.close();
    this._folder.set(folder);
    this._detail.set(null);
    this._status.set(null);
    this._error.set(null);
    this._lastOutput.set(null);
    this.heldSnapshot = null;

    this.subscriptions = [
      this.live.on('nodeStatus', (m) => this.applyNodeStatus(m)),
      this.live.on('itemStatus', (m) => this.applyItemStatus(m)),
      this.live.receipts(folder, (r) => this.onReceipt(r)),
      this.live.resynced((s) => this.onResync(s)),
      this.live.on('assembly', (m) => {
        if (m.kind === 'completed' && this.isMine(m.folder ?? folder)) void this.loadLastOutput();
      }),
    ];
    void this.live.joinProject(folder).then(() => this.seedItemsFromLive());
    void this.loadLastOutput();
    await this.load(folder);
  }

  /**
   * Reads detail + status again, staying in the group: viewing a History step, or going back to
   * current, changes what every read answers (`?at=`).
   */
  async reload(): Promise<void> {
    const folder = this._folder();
    if (folder !== null) await this.load(folder);
  }

  private async load(folder: string): Promise<void> {
    const load = ++this.loads;
    this._loading.set(true);
    try {
      const [detail, status] = await Promise.allSettled([
        this.api.get(folder),
        this.api.status(folder),
      ]);
      if (this._folder() !== folder || load !== this.loads) return;
      if (detail.status === 'fulfilled') this.setDetail(detail.value);
      if (status.status === 'fulfilled') this.applyStatus(status.value);
      const failure = [detail, status].find((r) => r.status === 'rejected');
      if (failure) this._error.set(messageOf(failure.reason));
    } finally {
      if (load === this.loads) this._loading.set(false);
    }
  }

  /** Leaves the group and drops pending refetches. Safe to call when nothing is open. */
  close(): void {
    for (const unsubscribe of this.subscriptions) unsubscribe();
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

  /** The stepper works without it, so a failure just leaves Export saying nothing was built. */
  private async loadLastOutput(): Promise<void> {
    const folder = this._folder();
    if (!folder) return;
    try {
      const [newest] = await this.assembly.outputs(folder);
      if (this._folder() === folder) this._lastOutput.set(newest ?? null);
    } catch {
      // Left as it was.
    }
  }

  private applyStatus(status: ProjectStatusDto): void {
    this._status.set(status);
    this._revision.set(status.revision);
    this._nodes.set(status.nodes);
    this._folderAudioRemaining.set(status.audio.remaining);
    const held = this.heldSnapshot;
    this.heldSnapshot = null;
    if (held !== null && held > status.revision) this.scheduleStatus();
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
    // Before `/status` answers there is no baseline to compare with (the revision still holds the
    // previous project's, or 0): hold the snapshot rather than read `/status` twice on every open.
    if (!this._status()) {
      this.heldSnapshot = Math.max(this.heldSnapshot ?? project.revision, project.revision);
      return;
    }
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

/** `formatDate(…, 'mediumDate')`: "19 Sep 2026" in the browser's locale. */
export function formatBuildDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  });
}

function messageOf(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}
