import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { firstValueFrom } from 'rxjs';

export interface AddVoiceDialogData {
  /** The name the voice takes when the field is left blank (Blazor: the character's name). */
  characterName: string;
}

export interface AddVoiceDialogResult {
  name: string;
  isGenerated: boolean;
}

/** Blazor's AddVoiceDialog: a name (default = the character) and the source, Reference Audio or Prompt. */
@Component({
  selector: 'app-add-voice-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatRadioModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Add voice</h2>
    <mat-dialog-content class="add-voice">
      <mat-form-field appearance="outline" class="add-voice__name" subscriptSizing="dynamic">
        <mat-label>Voice name</mat-label>
        <input
          matInput
          type="text"
          [placeholder]="data.characterName"
          [value]="name()"
          (input)="name.set($any($event.target).value)"
          (keydown.enter)="submit()"
          cdkFocusInitial
        />
      </mat-form-field>
      <mat-radio-group
        class="add-voice__source"
        aria-label="Voice source"
        [value]="isGenerated()"
        (change)="isGenerated.set($event.value)"
      >
        <mat-radio-button [value]="false">Reference audio</mat-radio-button>
        <mat-radio-button [value]="true">Prompt</mat-radio-button>
      </mat-radio-group>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button mat-flat-button type="button" data-action="add" (click)="submit()">Add</button>
    </mat-dialog-actions>
  `,
  styles: `
    .add-voice {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      min-width: min(360px, 80vw);
    }
    .add-voice__name {
      width: 100%;
    }
    .add-voice__source {
      display: flex;
      gap: var(--r2m-space-3);
    }
  `,
})
export class AddVoiceDialog {
  protected readonly data = inject<AddVoiceDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<AddVoiceDialog, AddVoiceDialogResult>>(MatDialogRef);
  protected readonly name = signal('');
  protected readonly isGenerated = signal(false);

  protected submit(): void {
    const name = this.name().trim() || this.data.characterName;
    this.ref.close({ name, isGenerated: this.isGenerated() });
  }
}

export async function openAddVoiceDialog(
  dialog: MatDialog,
  data: AddVoiceDialogData,
): Promise<AddVoiceDialogResult | null> {
  const ref = dialog.open<AddVoiceDialog, AddVoiceDialogData, AddVoiceDialogResult>(
    AddVoiceDialog,
    {
      data,
      restoreFocus: true,
    },
  );
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
