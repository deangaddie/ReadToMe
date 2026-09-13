import { Injectable, signal } from '@angular/core';

/**
 * Project titles the breadcrumb shows instead of the folder name. The project shell writes the
 * title once its detail loads; the app shell only reads, so it never fetches a project itself.
 */
@Injectable({ providedIn: 'root' })
export class ProjectTitles {
  private readonly _titles = signal<Record<string, string>>({});

  readonly titles = this._titles.asReadonly();

  set(folder: string, title: string): void {
    if (this._titles()[folder] === title) return;
    this._titles.update((all) => ({ ...all, [folder]: title }));
  }
}
