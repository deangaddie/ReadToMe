import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AudioProcessingApi, RecentAudioSample, toApiError } from '@app/api';
import { firstValueFrom } from 'rxjs';

export const SAMPLE_LIMIT = 20;
const SNIPPET_LENGTH = 90;

export function snippet(text: string): string {
  return text.length <= SNIPPET_LENGTH ? text : `${text.slice(0, SNIPPET_LENGTH)}…`;
}

/** "Hardin · Hardin Voice · Foundation" — character (or Narration), voice when known, project. */
export function provenance(sample: RecentAudioSample): string {
  return [sample.characterName ?? 'Narration', sample.voiceName, sample.projectTitle]
    .filter((part): part is string => !!part)
    .join(' · ');
}

/**
 * The A/B preview's sample picker (ticket 24): the most recently generated paragraph items that
 * still hold a Preview Source, newest first. Picking a row closes with it.
 */
@Component({
  selector: 'app-sample-picker-dialog',
  imports: [MatButtonModule, MatDialogModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Pick a sample</h2>
    <mat-dialog-content>
      @if (loading()) {
        <mat-progress-spinner mode="indeterminate" diameter="24" />
      } @else if (error(); as error) {
        <p class="sample-picker__empty" role="alert">Samples could not be loaded: {{ error }}</p>
      } @else if (!samples().length) {
        <p class="sample-picker__empty" data-role="empty">
          No preview samples yet — generate some audio and it'll show up here.
        </p>
      } @else {
        <ul class="sample-picker__list">
          @for (sample of samples(); track sample.itemId) {
            <li>
              <button
                type="button"
                class="sample-picker__row"
                [attr.data-item]="sample.itemId"
                (click)="ref.close(sample)"
              >
                <span class="sample-picker__text">{{ snippet(sample.text) }}</span>
                <span class="sample-picker__meta">{{ provenance(sample) }}</span>
              </button>
            </li>
          }
        </ul>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close(null)">Cancel</button>
    </mat-dialog-actions>
  `,
  styles: `
    :host {
      display: block;
      min-width: 420px;
      max-width: 640px;
    }
    .sample-picker__list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
    }
    .sample-picker__row {
      width: 100%;
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 1px solid transparent;
      border-radius: var(--r2m-radius-md);
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .sample-picker__row:hover,
    .sample-picker__row:focus-visible {
      background: var(--r2m-surface-low);
      border-color: var(--r2m-outline);
    }
    .sample-picker__text {
      font-size: var(--r2m-text-sm);
    }
    .sample-picker__meta,
    .sample-picker__empty {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
    }
    .sample-picker__empty {
      margin: 0;
    }
  `,
})
export class SamplePickerDialog {
  protected readonly ref =
    inject<MatDialogRef<SamplePickerDialog, RecentAudioSample | null>>(MatDialogRef);
  private readonly api = inject(AudioProcessingApi);

  protected readonly samples = signal<RecentAudioSample[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly snippet = snippet;
  protected readonly provenance = provenance;

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.samples.set(await this.api.recentSamples(SAMPLE_LIMIT));
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }
}

@Injectable({ providedIn: 'root' })
export class SamplePicker {
  private readonly dialog = inject(MatDialog);

  /** Resolves the picked sample, or null when dismissed. */
  async pick(): Promise<RecentAudioSample | null> {
    const ref = this.dialog.open<SamplePickerDialog, void, RecentAudioSample | null>(
      SamplePickerDialog,
      {
        autoFocus: 'first-tabbable',
        restoreFocus: true,
      },
    );
    return (await firstValueFrom(ref.afterClosed())) ?? null;
  }
}
