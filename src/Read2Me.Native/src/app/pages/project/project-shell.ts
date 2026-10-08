import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { untracked } from '@app/core/signals';
import { BookEditor } from '@app/pages/book/book-editor';
import { BookStore } from '@app/pages/book/book-store';
import { ProjectStore } from './project-store';
import '@app/core/outlet';

/**
 * `/projects/{folder}` (design §4, §9): loads the project once for every child route, holds the
 * `project:{folder}` hub group while any of them is active and provides the {@link ProjectStore}
 * they read through `use(ProjectStore, this)`. Child routes never join or fetch the project
 * themselves. The {@link BookStore} is provided here too, so the reader's window and expanded
 * tree nodes survive a visit to another project screen; the book page opens it with the folder.
 * The {@link BookEditor}, the reader's one write path, is provided beside it for the node menus.
 */
export class ProjectShell extends R2mElement {
  private readonly router = use(Router);
  private readonly store = provide(this, ProjectStore, new ProjectStore());
  private readonly book = provide(this, BookStore, new BookStore(this.store));
  private readonly editor = provide(this, BookEditor, new BookEditor(this.book));

  protected override connected(): void {
    // `params()` is a fresh object per navigation, so compare the folder itself: a query-only
    // change (`?mode=`, `?chapter=`) must not leave and rejoin the project.
    this.effect(() => {
      const folder = this.router.params()['folder'];
      untracked(() => {
        if (folder !== undefined && folder !== this.store.folder()) void this.store.open(folder);
      });
    });
    this.onDisconnect(() => {
      this.book.close();
      this.store.close();
    });
  }

  protected template() {
    return html`<r2m-outlet></r2m-outlet>`;
  }
}
define('r2m-project-shell', ProjectShell);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-project-shell': ProjectShell;
  }
}
