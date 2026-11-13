import {
  type CreateProjectRequest,
  type ProjectSummary,
  ProjectsApi,
  type SeriesDto,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import type { Unsubscribe } from '@app/live/live.service';
import { SeriesStore } from '@app/series/series-store';
import { membershipOf, seriesViews, shelfItems } from './series-shelf';

/**
 * The shelf's lists (design §6.1): the projects and the series, read together on every visit to
 * `/projects`, and again on every {@link SeriesStore} change while the page follows it (a Series
 * write from this page, the hub's `series` signal from any tab or agent, a reconnect). Series
 * writes go through `SeriesStore`, which waits for this re-read. The Series view is derived from
 * both reads here; the host has no combined read.
 */
export class ProjectsStore {
  private readonly api = use(ProjectsApi);
  private readonly seriesStore = use(SeriesStore);

  private readonly _projects = signal<ProjectSummary[]>([]);
  private readonly _series = signal<SeriesDto[]>([]);
  private readonly _loading = signal(false);
  private readonly _loaded = signal(false);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Sorted by title, case-insensitively, so the shelf reads like a bookshelf. */
  readonly projects = computed(() =>
    [...this._projects()].sort((a, b) =>
      a.title.localeCompare(b.title, undefined, { sensitivity: 'base' }),
    ),
  );
  /** Each series with its member projects in position order. */
  readonly series = computed(() => seriesViews(this._series(), this._projects()));
  /** Folder → its series and position. */
  readonly membership = computed(() => membershipOf(this.series()));
  /** The Series view's grid: series cards among the books in no series, by name. */
  readonly shelf = computed(() => shelfItems(this.series(), this._projects()));
  readonly loading = this._loading.asReadonly();
  /** False until the first load settles; the page shows skeletons while loading && !loaded. */
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();

  /** Re-reads on every Series change until the returned function is called. */
  follow(): Unsubscribe {
    return this.seriesStore.changes(() => this.reload());
  }

  /** Reads both lists; a slower, older read never overwrites a newer one. */
  async load(): Promise<void> {
    const seq = ++this.loadSeq;
    this._loading.set(true);
    try {
      const [projects, series] = await Promise.all([this.api.list(), this.seriesStore.list()]);
      if (seq !== this.loadSeq) return;
      this._projects.set(projects);
      this._series.set(series);
      this._error.set(null);
    } catch (error) {
      if (seq !== this.loadSeq) return;
      this._error.set(error instanceof Error ? error.message : String(error));
      throw error;
    } finally {
      if (seq === this.loadSeq) {
        this._loading.set(false);
        this._loaded.set(true);
      }
    }
  }

  /** Creates, then reloads so the new card carries the host's counters. Resolves the folder name. */
  async create(request: CreateProjectRequest): Promise<string> {
    const { folderName } = await this.api.create(request);
    await this.reload();
    return folderName;
  }

  /**
   * Deletes the project. A member book also leaves its series (#330), so `SeriesStore` raises a
   * `Changed` for that series, whatever the delete answered, and the shelf reads again from it;
   * a book in no series just reads again.
   */
  async delete(folder: string): Promise<void> {
    const seriesId = this.membership().get(folder)?.seriesId;
    try {
      await this.api.delete(folder);
      this._projects.update((list) => list.filter((p) => p.folderName !== folder));
    } finally {
      if (seriesId) await this.seriesStore.changed(seriesId);
    }
    if (!seriesId) await this.reload();
  }

  /** A reload after a write: its failure shows as the load error, not the write's. */
  private reload(): Promise<void> {
    return this.load().catch(() => undefined);
  }
}
