import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { BOOK_ACCEPT, BOOK_MAX_BYTES } from '@app/api';
import { toApiError } from '@app/api/api-client';
import { FileDrop, RejectedFile } from '@app/ui/file-drop/file-drop';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import {
  EMPTY_DRAFT,
  NewProjectDraft,
  NewProjectErrors,
  isDraftValid,
  rejectFile,
  setAuthor,
  setBookTitle,
  setFile,
  setTitle,
  toCreateRequest,
  validateDraft,
} from './new-project-form';
import { ProjectsStore } from './projects-store';

/** Closes with the new project's folder name, or `undefined` on cancel. */
export type NewProjectResult = string;

/**
 * New project dialog (ticket 08): book title, project title (follows the book title until edited),
 * author and an epub/txt drop. Create stays disabled until every field is valid; the upload runs
 * here so a host rejection (duplicate title, unreadable file) shows inline and the form survives.
 */
@Component({
  selector: 'app-new-project-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    FileDrop,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './new-project-dialog.html',
  styleUrl: './new-project-dialog.scss',
})
export class NewProjectDialog {
  private readonly ref = inject<MatDialogRef<NewProjectDialog, NewProjectResult>>(MatDialogRef);
  private readonly store = inject(ProjectsStore);

  readonly accept = BOOK_ACCEPT;
  readonly maxBytes = BOOK_MAX_BYTES;

  readonly draft = signal<NewProjectDraft>({ ...EMPTY_DRAFT });
  readonly touched = signal<Partial<Record<keyof NewProjectErrors, boolean>>>({});
  readonly errors = computed<NewProjectErrors>(() => validateDraft(this.draft()));
  readonly valid = computed(() => isDraftValid(this.draft()));
  readonly busy = signal(false);
  readonly submitError = signal<string | null>(null);

  error(key: keyof NewProjectErrors): string | null {
    return this.touched()[key] ? (this.errors()[key] ?? null) : null;
  }

  setBookTitle(value: string): void {
    this.draft.update((d) => setBookTitle(d, value));
    this.touch('bookTitle');
  }

  setTitle(value: string): void {
    this.draft.update((d) => setTitle(d, value));
    this.touch('title');
  }

  setAuthor(value: string): void {
    this.draft.update((d) => setAuthor(d, value));
    this.touch('author');
  }

  onFiles(files: File[]): void {
    const file = files[0];
    if (file) this.draft.update((d) => setFile(d, file));
  }

  onRejected(rejected: RejectedFile[]): void {
    const first = rejected[0];
    if (first) this.draft.update((d) => rejectFile(d, first));
  }

  async create(): Promise<void> {
    this.touched.set({ bookTitle: true, title: true, author: true, file: true });
    if (!this.valid() || this.busy()) return;
    this.busy.set(true);
    this.submitError.set(null);
    this.ref.disableClose = true;
    try {
      const folder = await this.store.create(toCreateRequest(this.draft()));
      this.ref.close(folder);
    } catch (error) {
      this.submitError.set(toApiError(error).toProblem().detail ?? 'Could not create the project');
    } finally {
      this.busy.set(false);
      this.ref.disableClose = false;
    }
  }

  cancel(): void {
    if (!this.busy()) this.ref.close();
  }

  private touch(key: keyof NewProjectErrors): void {
    this.touched.update((t) => ({ ...t, [key]: true }));
  }
}
