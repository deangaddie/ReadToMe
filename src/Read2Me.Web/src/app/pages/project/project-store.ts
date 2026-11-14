import type { BookFacets, ItemStatusEntry, ParagraphStatusEntry } from '@app/api';
import {
  AssemblyApi,
  type AssemblyOutputDto,
  type NodeStatusSummaryDto,
  type ProjectDetailDto,
  type ProjectStatusDto,
  ProjectsApi,
  StepView,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { entryFor, sameFolder } from '@app/live/folders';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { type FollowHandle, ProjectFeed } from '@app/live/project-feed';
import { ProjectTitles } from '@app/shell/project-titles';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { type PipelineStep, derivePipeline } from './pipeline-steps';

export { REFETCH_DEBOUNCE_MS };

/** Facets that move a `/status` value: structure, speakers, audio, reviews, cast, voices and checkpoints. */
const STATUS_FACETS: readonly BookFacets[] = [
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
const DETAIL_FACETS: readonly BookFacets[] = ['Characters', 'Narrator', 'ProjectPolicy'];

/** What the pipeline and the book page read: the node roll-ups and the queue's item status. */
interface StatusMaps {
  readonly nodes: Record<string, NodeStatusSummaryDto | null>;
  readonly folderAudioRemaining: number;
  readonly paragraphs: Record<string, ParagraphStatusEntry | null>;
  readonly items: Record<string, ItemStatusEntry | null>;
}

const NO_STATUS: StatusMaps = { nodes: {}, folderAudioRemaining: 0, paragraphs: {}, items: {} };

/**
 * One project's state for every route under `/projects/{folder}` (design §9), provided by the
 * project shell. Holds the detail and the `/status` bootstrap, and turns receipts into refetches —
 * never into entity patches (spec D6). The node and item status maps are not its own: they are the
 * live service's entry for the project once the hub has answered for it (see {@link statusMaps}).
 * It follows the project through two {@link ProjectFeed} handles, both baselined by every `/status`
 * answer: a receipt whose facets move a count refetches `/status`, one that touches the narrator
 * or project policy refetches the detail, and a gap or a catch-up refetches both. It also owns
 * the nine-step {@link pipeline} and its last input: the newest build, read on open and again when
 * this project's assembly completes, plus the hub's voice-batch and assembly flags.
 */
export class ProjectStore {
  private readonly api = use(ProjectsApi);
  private readonly assembly = use(AssemblyApi);
  private readonly live = use(LiveService);
  private readonly feed = use(ProjectFeed);
  private readonly titles = use(ProjectTitles);
  private readonly stepView = use(StepView);

  private subscriptions: Unsubscribe[] = [];
  /** The `/status` and detail refetches; both take every `/status` revision as their baseline. */
  private following: FollowHandle[] = [];
  /** Bumped per detail + status read, so a read overtaken by a newer one (at another step) lands nowhere. */
  private loads = 0;

  private readonly _folder = signal<string | null>(null);
  private readonly _detail = signal<ProjectDetailDto | null>(null);
  private readonly _status = signal<ProjectStatusDto | null>(null);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);
  /** Newest assembled audiobook, for the Export step; null when none or the read failed. */
  private readonly _lastOutput = signal<AssemblyOutputDto | null>(null);

  readonly folder = this._folder.asReadonly();
  readonly detail = this._detail.asReadonly();
  /** The last `/status` read; its `nodes` and `audio.remaining` go stale — read {@link nodes} instead. */
  readonly status = this._status.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly error = this._error.asReadonly();
  /**
   * Why nothing in the project can be changed (ticket 32): it is viewed at a History step. Null on
   * the current project. Every edit control renders disabled with this as its reason.
   */
  readonly readOnly = computed(() => this.stepView.readOnly(this._folder()));

  /**
   * Where the status maps come from. At a History step, the last `/status` read, which read `?at=`
   * the step: live deltas are today's and must not move its pills, and the queue's item status is
   * today's too, so there is none. Otherwise the live entry once the hub has answered for it; until
   * then `/status`, so the pipeline does not flash empty.
   */
  private readonly statusMaps = computed<StatusMaps>(() => {
    const folder = this._folder();
    const entry = folder === null ? undefined : entryFor(this.live.projects(), folder);
    if (this.readOnly() === null && entry?.seeded) return entry;
    const status = this._status();
    if (!status) return NO_STATUS;
    return { ...NO_STATUS, nodes: status.nodes, folderAudioRemaining: status.audio.remaining };
  });

  readonly nodes = computed(() => this.statusMaps().nodes);
  readonly folderAudioRemaining = computed(() => this.statusMaps().folderAudioRemaining);
  readonly paragraphs = computed(() => this.statusMaps().paragraphs);
  readonly items = computed(() => this.statusMaps().items);

  /** Items the audio queue reports queued or processing for this project. */
  readonly audioInFlight = computed(
    () =>
      Object.values(this.items()).filter(
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
      nodes: this.nodes(),
      folderAudioRemaining: this.folderAudioRemaining(),
      audioInFlight: this.audioInFlight(),
      voiceBatchRunning: this.live.voiceBatch().isRunning,
      assemblyRunning: this.live.assembly().isRunning,
      lastBuild: output
        ? { date: formatBuildDate(output.createdAt), isPartial: output.isPartial }
        : null,
    });
  });

  /** Follows the project (which joins its group) and loads detail + status. */
  async open(folder: string): Promise<void> {
    this.close();
    this._folder.set(folder);
    this._detail.set(null);
    this._status.set(null);
    this._error.set(null);
    this._lastOutput.set(null);

    this.subscriptions = [
      this.live.on('assembly', (m) => {
        if (m.kind === 'Completed' && sameFolder(m.folder ?? folder, folder))
          void this.loadLastOutput();
      }),
    ];
    const awaitStatus = { baseline: 'await' } as const;
    this.following = [
      this.feed.refetchOn(folder, STATUS_FACETS, () => this.refreshStatus(), awaitStatus),
      this.feed.refetchOn(folder, DETAIL_FACETS, () => this.refreshDetail(), awaitStatus),
    ];
    void this.loadLastOutput();
    await this.load(folder);
  }

  /**
   * Reads detail + status again, staying in the group: viewing a History step, or going back to
   * current, changes what every read answers (`?at=`).
   */
  async reload(): Promise<void> {
    const folder = this._folder();
    if (folder === null) return;
    await this.load(folder);
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

  /** Stops following, which leaves the group, and drops pending refetches. Safe when nothing is open. */
  close(): void {
    for (const unsubscribe of this.subscriptions) unsubscribe();
    this.subscriptions = [];
    for (const handle of this.following) handle.close();
    this.following = [];
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
    for (const handle of this.following) handle.baseline(status.revision);
  }
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
