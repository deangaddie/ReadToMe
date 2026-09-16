import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { CharacterSummaryDto, Guid, VoicesApi } from '@app/api';
import { firstValueFrom } from 'rxjs';
import {
  MergeForm,
  initialMergeForm,
  lostVoicesWarning,
  mergeCandidates,
  validateMerge,
} from './merge-options';

export interface MergeDialogData {
  folder: string;
  merged: CharacterSummaryDto;
  rows: readonly CharacterSummaryDto[];
}

/**
 * Merge one character into another (research §4 "Merge"): survivor select, "add name as alias"
 * on by default, and a warning naming the voices the merged character takes with it. Resolves the
 * form to post, or nothing on Cancel.
 */
@Component({
  selector: 'app-merge-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Merge characters</h2>
    <mat-dialog-content class="merge">
      <p>
        Merge <strong>{{ data.merged.name }}</strong> into another character. All lines currently
        attributed to {{ data.merged.name }} will be re-attributed to the survivor.
      </p>
      <mat-form-field appearance="outline" class="merge__survivor">
        <mat-label>Merge into</mat-label>
        <mat-select
          [value]="form().survivorId"
          (selectionChange)="patch({ survivorId: $event.value })"
        >
          @for (c of candidates; track c.id) {
            <mat-option [value]="c.id">{{ c.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-checkbox
        [checked]="form().addNameAsAlias"
        (change)="patch({ addNameAsAlias: $event.checked })"
      >
        Add "{{ data.merged.name }}" as an alias of the survivor
      </mat-checkbox>
      @if (warning(); as warning) {
        <p class="merge__warning" role="alert">{{ warning }}</p>
      }
      @if (error(); as error) {
        <p class="merge__error">{{ error }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        class="merge__submit"
        [disabled]="error() !== null"
        (click)="submit()"
      >
        Merge
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .merge {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      max-width: 480px;
    }
    .merge p {
      margin: 0;
    }
    .merge__survivor {
      width: 100%;
    }
    .merge__warning {
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 1px solid var(--r2m-status-warn);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-status-warn-soft);
      font-size: var(--r2m-text-sm);
    }
    .merge__error {
      color: var(--r2m-status-error);
      font-size: var(--r2m-text-sm);
    }
  `,
})
export class MergeDialog {
  protected readonly data = inject<MergeDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<MergeDialog, MergeForm>>(MatDialogRef);
  private readonly voices = inject(VoicesApi);

  protected readonly candidates = mergeCandidates(this.data.rows, this.data.merged.id);
  protected readonly form = signal<MergeForm>(initialMergeForm(this.candidates));
  protected readonly error = computed(() =>
    validateMerge(this.form(), this.candidates, this.data.merged.id),
  );

  private readonly voiceNames = signal<string[]>([]);
  protected readonly warning = computed(() =>
    lostVoicesWarning(this.data.merged.name, this.voiceNames()),
  );

  constructor() {
    // The roster row only counts voices; the warning names them.
    if (this.data.merged.voiceCount > 0) {
      void this.voices
        .list(this.data.folder, this.data.merged.id)
        .then((v) => this.voiceNames.set(v.voices.map((x) => x.name)))
        .catch(() => this.voiceNames.set(Array(this.data.merged.voiceCount).fill('(voice)')));
    }
  }

  protected patch(change: Partial<MergeForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected submit(): void {
    if (this.error() === null) this.ref.close(this.form());
  }
}

/** Opens the merge dialog; resolves the survivor choice, or null when cancelled. */
export async function openMergeDialog(
  dialog: MatDialog,
  data: MergeDialogData,
): Promise<{ survivorId: Guid; addNameAsAlias: boolean } | null> {
  const ref = dialog.open<MergeDialog, MergeDialogData, MergeForm>(MergeDialog, {
    data,
    autoFocus: 'first-tabbable',
    restoreFocus: true,
  });
  const result = await firstValueFrom(ref.afterClosed());
  return result?.survivorId ? { survivorId: result.survivorId, addNameAsAlias: result.addNameAsAlias } : null;
}
