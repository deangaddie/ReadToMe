import {
  type CreateProjectRequest,
  type ProjectSummary,
  ProjectsApi,
  type SeriesDto,
  SeriesApi,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import { membershipOf, seriesViews, shelfItems } from './series-shelf';

/**
 * The shelf's lists (design §6.1): the projects and the series, read together on every visit to
 * `/projects`, after this page's own writes, and again on the hub's `series` signal while the page
 * follows it (a registry write or a member book's commit, from any tab or agent). The Series view
 * is derived from both reads here; the host has no combined read.
 */
export class ProjectsStore {
  private readonly api = use(ProjectsApi);
  private readonly seriesApi = use(SeriesApi);
  private readonly live = use(LiveService);

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

  /** Re-reads on every `series` hub signal until the returned function is called. */
  follow(): Unsubscribe {
    return this.live.on('series', () => void this.load().catch(() => undefined));
  }

  /** Reads both lists; a slower, older read never overwrites a newer one. */
  async load(): Promise<void> {
    const seq = ++this.loadSeq;
    this._loading.set(true);
    try {
      const [projects, series] = await Promise.all([this.api.list(), this.seriesApi.list()]);
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

  /** Deletes the project; the host drops its registry entry, so the series are read again. */
  async delete(folder: string): Promise<void> {
    await this.api.delete(folder);
    this._projects.update((list) => list.filter((p) => p.folderName !== folder));
    await this.reload();
  }

  /** A new series, empty or (with `folder`) with that book as Book 1. Resolves its id. */
  async createSeries(name: string, folder?: string): Promise<string> {
    const id = await this.seriesApi.create(name, folder);
    await this.reload();
    return id;
  }

  async renameSeries(id: string, name: string): Promise<void> {
    await this.seriesApi.rename(id, name);
    await this.reload();
  }

  async deleteSeries(id: string): Promise<void> {
    await this.seriesApi.delete(id);
    await this.reload();
  }

  /** Join, or Move to with `move`; resolves the assigned Series position. */
  async join(id: string, folder: string, move: boolean): Promise<number> {
    const position = await this.seriesApi.join(id, folder, move);
    await this.reload();
    return position;
  }

  async leave(id: string, folder: string): Promise<void> {
    await this.seriesApi.leave(id, folder);
    await this.reload();
  }

  async savePositions(id: string, positions: Record<string, number>): Promise<void> {
    await this.seriesApi.savePositions(id, positions);
    await this.reload();
  }

  /** A reload after a write that already landed: its failure shows as the load error, not the write's. */
  private reload(): Promise<void> {
    return this.load().catch(() => undefined);
  }
}
