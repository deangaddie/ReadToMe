import { signal } from '@app/core/signals';

/**
 * Project titles the breadcrumb shows instead of the folder name. The project store writes the
 * title once its detail loads; the project route's title function only reads, so the shell never
 * fetches a project itself.
 */
export class ProjectTitles {
  private readonly _titles = signal<Record<string, string>>({});

  readonly titles = this._titles.asReadonly();

  set(folder: string, title: string): void {
    if (this._titles()[folder] === title) return;
    this._titles.update((all) => ({ ...all, [folder]: title }));
  }
}
