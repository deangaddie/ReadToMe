import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { PromptBatchScope } from './voice-logic';

/**
 * Blazor's GenerateVoicesScopeDialog: shown before the prompt batch when any character already
 * has voices. "Clear and regenerate all" is destructive (every voice, its audio and rules go first).
 */
@Component({
  selector: 'app-voice-scope-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Generate voice prompts</h2>
    <mat-dialog-content class="voice-scope">
      <p>Some characters already have voices. Choose what to generate.</p>
      <p class="voice-scope__warning">
        <strong>Clear and regenerate all</strong> deletes every character's existing voices (and
        their audio and rules) first. This cannot be undone.
      </p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button
        mat-stroked-button
        type="button"
        data-scope="only-without"
        (click)="ref.close('only-without')"
      >
        Only characters without voices
      </button>
      <button
        mat-flat-button
        type="button"
        class="voice-scope__destructive"
        data-scope="regenerate-all"
        (click)="ref.close('regenerate-all')"
      >
        <mat-icon>delete_sweep</mat-icon> Clear and regenerate all
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .voice-scope {
      max-width: 480px;
    }
    .voice-scope__warning {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .voice-scope__destructive {
      --mdc-filled-button-container-color: var(--r2m-status-error);
      --mdc-filled-button-label-text-color: var(--mat-sys-on-error);
      --mat-filled-button-icon-color: var(--mat-sys-on-error);
    }
  `,
})
export class VoiceScopeDialog {
  protected readonly ref = inject<MatDialogRef<VoiceScopeDialog, PromptBatchScope>>(MatDialogRef);
}

/** Resolves the chosen scope, or null on Cancel / Escape / backdrop. */
export async function openVoiceScopeDialog(dialog: MatDialog): Promise<PromptBatchScope | null> {
  const ref = dialog.open<VoiceScopeDialog, void, PromptBatchScope>(VoiceScopeDialog, {
    restoreFocus: true,
  });
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
