import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import {
  COVER_ACCEPT,
  COVER_MAX_BYTES,
  ProjectsApi,
  UpdateProjectRequest,
  toApiError,
  workspaceUrl,
} from '@app/api';
import { FileDrop, RejectedFile } from '@app/ui/file-drop/file-drop';
import { InlineEdit } from '@app/ui/inline-edit/inline-edit';
import { KeyValue, KeyValueRow } from '@app/ui/key-value/key-value';
import { placeholderGradient, projectInitials } from '@app/ui/project-card/project-card';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { ProjectStore } from './project-store';

/**
 * The project card beside the stepper (design §6.2): cover upload/remove, inline title and author,
 * file name and type, the narrator-only policy and who narrates. Every write waits for the host:
 * the PATCH answers with the new detail, the others refetch it.
 */
@Component({
  selector: 'app-project-details-panel',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatSlideToggleModule,
    RouterLink,
    FileDrop,
    InlineEdit,
    KeyValue,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.detail(); as d) {
      <div class="details__cover" [style.background]="coverUrl() ? null : placeholder()">
        @if (coverUrl(); as url) {
          <img class="details__image" [src]="url" [alt]="d.title + ' cover'" />
        } @else {
          <span class="details__initials" aria-hidden="true">{{ initials() }}</span>
        }
      </div>
      <div class="details__cover-actions">
        <r2m-file-drop
          class="details__drop"
          [accept]="coverAccept"
          [maxBytes]="coverMaxBytes"
          [label]="d.coverImage ? 'Replace cover' : 'Add a cover'"
          hint="JPG, PNG or WebP, up to 10 MB"
          (files)="uploadCover($event)"
          (rejected)="rejectCover($event)"
        />
        @if (d.coverImage) {
          <button
            mat-button
            type="button"
            class="details__remove-cover"
            [disabled]="busy()"
            (click)="removeCover()"
          >
            <mat-icon>hide_image</mat-icon> Remove cover
          </button>
        }
      </div>

      <div class="details__field">
        <span class="details__label">Title</span>
        <r2m-inline-edit
          class="details__title"
          [value]="d.title"
          placeholder="Title"
          required
          (save)="update({ title: $event })"
        />
      </div>
      <div class="details__field">
        <span class="details__label">Author</span>
        <r2m-inline-edit
          class="details__author"
          [value]="d.author"
          placeholder="Author"
          (save)="update({ author: $event })"
        />
      </div>

      <r2m-key-value dense [rows]="rows()" />
      <r2m-status-chip class="details__type" status="info" [label]="d.fileType" compact />

      <mat-slide-toggle
        class="details__narrator-only"
        [checked]="d.narratorOnlyMode"
        [disabled]="busy()"
        (change)="setNarratorOnly($event.checked)"
      >
        Narrator only
      </mat-slide-toggle>
      <p class="details__hint">Only narration gets audio; dialog is read by the narrator.</p>

      <p class="details__narrator">
        <mat-icon aria-hidden="true">record_voice_over</mat-icon>
        <span>{{
          d.narrator.isLinked ? 'Narrated by ' + d.narrator.displayName : 'Own narrator voice'
        }}</span>
        <a class="details__cast-link" [routerLink]="castLink()">Cast</a>
      </p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
    }
    :host:empty {
      display: none;
    }
    .details__cover {
      position: relative;
      display: grid;
      place-items: center;
      aspect-ratio: 3 / 4;
      width: 100%;
      max-width: 200px;
      margin: 0 auto;
      border-radius: var(--r2m-radius-md);
      overflow: hidden;
      color: #fff;
    }
    .details__image {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      object-fit: cover;
    }
    .details__initials {
      font-size: var(--r2m-text-2xl);
      font-weight: 700;
    }
    .details__cover-actions {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--r2m-space-1);
    }
    .details__drop {
      width: 100%;
      padding: var(--r2m-space-3);
    }
    .details__field {
      display: flex;
      flex-direction: column;
      gap: 2px;
      min-width: 0;
    }
    .details__label,
    .details__hint {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .details__title {
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .details__hint {
      margin: calc(-1 * var(--r2m-space-2)) 0 0;
    }
    .details__type {
      align-self: flex-start;
    }
    .details__narrator {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      margin: 0;
      font-size: var(--r2m-text-md);
    }
    .details__narrator mat-icon {
      color: var(--r2m-text-muted);
    }
    .details__cast-link {
      margin-left: auto;
      color: var(--r2m-accent);
    }
  `,
})
export class ProjectDetailsPanel {
  readonly store = inject(ProjectStore);
  private readonly api = inject(ProjectsApi);
  private readonly toast = inject(ToastService);

  readonly coverAccept = COVER_ACCEPT;
  readonly coverMaxBytes = COVER_MAX_BYTES;

  readonly busy = signal(false);
  /** Bumped after an upload: a replaced cover may keep its file name. */
  private readonly coverVersion = signal<number | null>(null);

  readonly coverUrl = computed(() => {
    const d = this.store.detail();
    return d?.coverImage ? workspaceUrl(d.folderName, d.coverImage, this.coverVersion()) : null;
  });
  readonly initials = computed(() => projectInitials(this.store.detail()?.title ?? ''));
  readonly placeholder = computed(() => placeholderGradient(this.store.detail()?.folderName ?? ''));

  readonly rows = computed<KeyValueRow[]>(() => {
    const d = this.store.detail();
    return d
      ? [
          { label: 'Book title', value: d.bookTitle },
          { label: 'File', value: d.filename, mono: true },
        ]
      : [];
  });

  readonly castLink = computed(() => {
    const d = this.store.detail();
    if (!d) return [];
    const base = ['/projects', d.folderName, 'cast'];
    return d.narrator.isLinked ? [...base, d.narrator.characterId] : base;
  });

  async update(request: UpdateProjectRequest): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => this.store.setDetail(await this.api.update(folder, request)));
  }

  async setNarratorOnly(enabled: boolean): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => {
      await this.api.setNarratorOnlyMode(folder, enabled);
      await this.store.refreshDetail();
    });
  }

  async uploadCover(files: File[]): Promise<void> {
    const folder = this.store.folder();
    const file = files[0];
    if (!folder || !file) return;
    await this.write(async () => {
      await this.api.uploadCover(folder, file);
      this.coverVersion.set(Date.now());
      await this.store.refreshDetail();
    });
  }

  rejectCover(rejected: RejectedFile[]): void {
    const reason = rejected[0]?.reason;
    this.toast.warn(
      reason === 'size'
        ? 'Cover image must be 10 MB or smaller.'
        : 'Use a .jpg, .png or .webp image.',
    );
  }

  async removeCover(): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => {
      await this.api.deleteCover(folder);
      await this.store.refreshDetail();
    });
  }

  private async write(command: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await command();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      // A refused toggle or edit must snap back to what the host holds.
      await this.store.refreshDetail().catch(() => undefined);
    } finally {
      this.busy.set(false);
    }
  }
}
