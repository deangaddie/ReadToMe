import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { BookEditor } from '../book/book-editor';
import { BookStore } from '../book/book-store';
import { AudioGenerator } from '../book/audio-generator';
import { AudioSelectionStore } from '../book/audio-selection-store';
import { SelectionStore } from '../book/selection-store';
import { SpeakerAssigner } from '../book/speaker-assigner';
import { ProjectStore } from './project-store';

/**
 * `/projects/{folder}` (ticket 09, design §4, §9): loads the project once for every child route,
 * holds the `project:{folder}` hub group while any of them is active and provides the
 * {@link ProjectStore} they read. Child routes never join or fetch the project themselves.
 * The {@link BookStore} lives here too so the reader keeps its place across child routes; it
 * loads lazily when the book page first opens it. The {@link BookEditor} is its write side, the
 * {@link SelectionStore} its paragraph selection and the {@link SpeakerAssigner} its speaker writes;
 * the {@link AudioSelectionStore} and {@link AudioGenerator} are Audio mode's pair (ticket 13).
 */
@Component({
  selector: 'app-project-shell',
  imports: [RouterOutlet],
  providers: [
    ProjectStore,
    BookStore,
    BookEditor,
    SelectionStore,
    AudioSelectionStore,
    SpeakerAssigner,
    AudioGenerator,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<router-outlet />`,
})
export class ProjectShell {
  private readonly store = inject(ProjectStore);
  private readonly book = inject(BookStore);
  private readonly selection = inject(SelectionStore);
  private readonly audioSelection = inject(AudioSelectionStore);

  /** Route param, bound by `withComponentInputBinding`. */
  readonly folder = input.required<string>();

  constructor() {
    effect((onCleanup) => {
      const folder = this.folder();
      untracked(() => void this.store.open(folder));
      onCleanup(() => {
        this.selection.reset();
        this.audioSelection.reset();
        this.book.close();
        this.store.close();
      });
    });
  }
}
