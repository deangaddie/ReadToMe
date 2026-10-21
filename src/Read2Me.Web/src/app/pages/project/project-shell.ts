import { html } from 'lit-html';
import { keyed } from 'lit-html/directives/keyed.js';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import type { Guid } from '@app/api';
import { provide, use } from '@app/core/services';
import { signal, untracked } from '@app/core/signals';
import { AudioGenerator } from '@app/pages/book/audio-generator';
import { BookEditor } from '@app/pages/book/book-editor';
import { BookStore } from '@app/pages/book/book-store';
import { AudioSelectionStore, SelectionStore } from '@app/pages/book/selection-store';
import { SpeakerAssigner } from '@app/pages/book/speaker-assigner';
import { HistorySheetState } from '@app/history/history-sheet-state';
import { HistoryStore } from '@app/history/history-store';
import { ProjectGuide } from '@app/shell/project-guide';
import { nextStep } from './next-step';
import { ProjectStore } from './project-store';
import { StepRunner } from './step-runner';
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
 * DOM ancestry. The selections are forgotten when the shell leaves the project. The
 * {@link StepRunner} runs Pipeline actions for the Overview rows, and the shell publishes the next
 * step with it into the root {@link ProjectGuide} for the app bar's next-step pill. The project's
 * {@link HistoryStore} follows its History on the hub and is published into the root
 * {@link HistorySheetState} for the app bar's History button and sheet. Viewing a History step, or
 * going back to current, rereads the project and mounts the page afresh, so everything on it reads
 * at the step (`?at=`); the selections and the reader's window are dropped with it.
 */
export class ProjectShell extends R2mElement {
  private readonly router = use(Router);
  private readonly guide = use(ProjectGuide);
  private readonly historySheet = use(HistorySheetState);
  private readonly history = provide(this, HistoryStore, new HistoryStore());
  private readonly store = provide(this, ProjectStore, new ProjectStore());
  private readonly book = provide(this, BookStore, new BookStore(this.store));
  private readonly editor = provide(this, BookEditor, new BookEditor(this.book));
  private readonly selection = provide(this, SelectionStore, new SelectionStore());
  private readonly audioSelection = provide(this, AudioSelectionStore, new AudioSelectionStore());
  private readonly runner = provide(this, StepRunner, new StepRunner(this.store));
  /** The step the page was mounted at; the outlet is keyed on it. */
  private readonly shownAt = signal<Guid | null>(null);

  constructor() {
    super();
    // Provided for the rows and the page only: the shell itself never reads them, so no field holds them.
    provide(this, SpeakerAssigner, new SpeakerAssigner(this.editor, this.book, this.store));
    provide(this, AudioGenerator, new AudioGenerator(this.editor, this.book));
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
        this.history.open(folder);
        this.historySheet.publish(folder, this.history);
      });
    });
    this.effect(() => {
      const folder = this.store.folder();
      const steps = this.store.pipeline();
      untracked(() => {
        if (folder === null) return;
        if (steps) this.guide.publish(folder, nextStep(steps), this.runner);
        else this.guide.clear(folder);
      });
    });
    this.effect(() => {
      const viewing = this.history.viewing();
      untracked(() => {
        if (viewing === this.shownAt()) return;
        this.resetSelections();
        this.book.close();
        // Leaving a project goes back to current too; the new project's open reads it already.
        if (this.history.folder() === this.store.folder()) void this.store.reload();
        this.shownAt.set(viewing);
      });
    });
    this.onDisconnect(() => {
      const folder = this.store.folder();
      if (folder !== null) {
        this.guide.clear(folder);
        this.historySheet.clear(folder);
      }
      this.history.close();
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
    return keyed(this.shownAt(), html`<r2m-outlet></r2m-outlet>`);
  }
}
define('r2m-project-shell', ProjectShell);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-project-shell': ProjectShell;
  }
}
