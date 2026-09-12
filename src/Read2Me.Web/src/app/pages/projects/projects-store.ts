import { Injectable, computed, inject, signal } from '@angular/core';
import { CreateProjectRequest, ProjectSummary, ProjectsApi } from '@app/api';

/**
 * The shelf's list (ticket 08). Deliberately simple: it refreshes when the page loads and after
 * its own create/delete; there is no hub subscription, because the list only changes through this
 * UI or an agent, and either way the next visit to `/projects` reloads it.
 */
@Injectable({ providedIn: 'root' })
export class ProjectsStore {
  private readonly api = inject(ProjectsApi);

  private readonly _projects = signal<ProjectSummary[]>([]);
  private readonly _loading = signal(false);
  private readonly _loaded = signal(false);
  private readonly _error = signal<string | null>(null);

  /** Sorted by title, case-insensitively, so the shelf reads like a bookshelf. */
  readonly projects = computed(() =>
    [...this._projects()].sort((a, b) =>
      a.title.localeCompare(b.title, undefined, { sensitivity: 'base' }),
    ),
  );
  readonly loading = this._loading.asReadonly();
  /** False until the first load settles; the page shows skeletons while loading && !loaded. */
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();

  async load(): Promise<void> {
    this._loading.set(true);
    try {
      this._projects.set(await this.api.list());
      this._error.set(null);
    } catch (error) {
      this._error.set(error instanceof Error ? error.message : String(error));
      throw error;
    } finally {
      this._loading.set(false);
      this._loaded.set(true);
    }
  }

  /** Creates, then reloads so the new card carries the host's counters. Resolves the folder name. */
  async create(request: CreateProjectRequest): Promise<string> {
    const { folderName } = await this.api.create(request);
    await this.load();
    return folderName;
  }

  async delete(folder: string): Promise<void> {
    await this.api.delete(folder);
    this._projects.update((list) => list.filter((p) => p.folderName !== folder));
  }
}
