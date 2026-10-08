import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { untracked } from '@app/core/signals';
import { BookEditor } from '@app/pages/book/book-editor';
import { BookStore } from '@app/pages/book/book-store';
import { AudioSelectionStore, SelectionStore } from '@app/pages/book/selection-store';
import { SpeakerAssigner } from '@app/pages/book/speaker-assigner';
import { ProjectStore } from './project-store';
import '@app/core/outlet';

/**
 * `/projects/{folder}` (design §4, §9): loads the project once for every child route, holds the
 * `project:{folder}` hub group while any of them is active and provides the {@link ProjectStore}
 * they read through `use(ProjectStore, this)`. Child routes never join or fetch the project
 * themselves. The {@link BookStore} is provided here too, so the reader's window and expanded
 * tree nodes survive a visit to another project screen; the book page opens it with the folder.
 * The {@link BookEditor}, the reader's one write path, is provided beside it for the node menus,
 * with the reader's two selections ({@link SelectionStore} for paragraphs,
 * {@link AudioSelectionStore} for items) and the {@link SpeakerAssigner} the rows reach through
 * DOM ancestry. The selections are forgotten when the shell leaves the project.
 */
export class ProjectShell extends R2mElement {
  private readonly router = use(Router);
  private readonly store = provide(this, ProjectStore, new ProjectStore());
  private readonly book = provide(this, BookStore, new BookStore(this.store));
  private readonly editor = provide(this, BookEditor, new BookEditor(this.book));
  private readonly selection = provide(this, SelectionStore, new SelectionStore());
  private readonly audioSelection = provide(this, AudioSelectionStore, new AudioSelectionStore());

  constructor() {
    super();
    // Provided for the rows only: the shell itself never reads it, so no field holds it.
    provide(this, SpeakerAssigner, new SpeakerAssigner(this.editor, this.book, this.store));
  }

  protected override connected(): void {
    // `params()` is a fresh object per navigation, so compare the folder itself: a query-only
    // change (`?mode=`, `?chapter=`) must not leave and rejoin the project.
    this.effect(() => {
      const folder = this.router.params()['folder'];
      untracked(() => {
        if (folder === undefined || folder === this.store.folder()) return;
        this.resetSelections();
        void this.store.open(folder);
      });
    });
    this.onDisconnect(() => {
      this.resetSelections();
      this.book.close();
      this.store.close();
    });
  }

  private resetSelections(): void {
    this.selection.reset();
    this.audioSelection.reset();
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
