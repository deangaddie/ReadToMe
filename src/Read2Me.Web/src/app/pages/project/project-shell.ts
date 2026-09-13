import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { BookStore } from '../book/book-store';
import { ProjectStore } from './project-store';

/**
 * `/projects/{folder}` (ticket 09, design §4, §9): loads the project once for every child route,
 * holds the `project:{folder}` hub group while any of them is active and provides the
 * {@link ProjectStore} they read. Child routes never join or fetch the project themselves.
 * The {@link BookStore} lives here too so the reader keeps its place across child routes; it
 * loads lazily when the book page first opens it.
 */
@Component({
  selector: 'app-project-shell',
  imports: [RouterOutlet],
  providers: [ProjectStore, BookStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet />`,
})
export class ProjectShell {
  private readonly store = inject(ProjectStore);
  private readonly book = inject(BookStore);

  /** Route param, bound by `withComponentInputBinding`. */
  readonly folder = input.required<string>();

  constructor() {
    effect((onCleanup) => {
      const folder = this.folder();
      untracked(() => void this.store.open(folder));
      onCleanup(() => {
        this.book.close();
        this.store.close();
      });
    });
  }
}
