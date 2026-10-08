import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { untracked } from '@app/core/signals';
import { BookStore } from '@app/pages/book/book-store';
import { ProjectStore } from './project-store';
import '@app/core/outlet';

/**
 * `/projects/{folder}` (design §4, §9): loads the project once for every child route, holds the
 * `project:{folder}` hub group while any of them is active and provides the {@link ProjectStore}
 * they read through `use(ProjectStore, this)`. Child routes never join or fetch the project
 * themselves. The {@link BookStore} is provided here too, so the reader's window and expanded
 * tree nodes survive a visit to another project screen; the book page opens it with the folder.
 */
export class ProjectShell extends R2mElement {
  private readonly router = use(Router);
  private readonly store = provide(this, ProjectStore, new ProjectStore());
  private readonly book = provide(this, BookStore, new BookStore(this.store));

  protected override connected(): void {
    this.effect(() => {
      const folder = this.router.params()['folder'];
      untracked(() => {
        if (folder !== undefined) void this.store.open(folder);
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
