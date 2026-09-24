import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';

export interface TextPromptOptions {
  title: string;
  label?: string;
  initial?: string;
  multiline?: boolean;
  required?: boolean;
  placeholder?: string;
  confirmLabel?: string;
}

/**
 * Single/multiline text prompt (design §7) for titles, item text and inserted items. Open it via
 * `PromptService.text()`; resolves the trimmed text, or null on Cancel/Escape/backdrop.
 */
@Component({
  selector: 'r2m-text-prompt-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, TextFieldModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-text-prompt-dialog' },
  template: `
    <h2 mat-dialog-title class="r2m-text-prompt-dialog__title">{{ data.title }}</h2>
    <mat-dialog-content>
      <mat-form-field class="r2m-text-prompt-dialog__field" appearance="outline">
        @if (data.label) {
          <mat-label>{{ data.label }}</mat-label>
        }
        @if (data.multiline) {
          <textarea
            matInput
            cdkTextareaAutosize
            cdkAutosizeMinRows="3"
            cdkAutosizeMaxRows="12"
            cdkFocusInitial
            class="r2m-text-prompt-dialog__input"
            [placeholder]="data.placeholder ?? ''"
            [value]="value()"
            (input)="value.set($any($event.target).value)"
          ></textarea>
        } @else {
          <input
            matInput
            type="text"
            cdkFocusInitial
            class="r2m-text-prompt-dialog__input"
            [placeholder]="data.placeholder ?? ''"
            [value]="value()"
            (input)="value.set($any($event.target).value)"
            (keydown.enter)="submit()"
          />
        }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button
        mat-button
        type="button"
        class="r2m-text-prompt-dialog__cancel"
        (click)="ref.close(null)"
      >
        Cancel
      </button>
      <button
        mat-flat-button
        type="button"
        class="r2m-text-prompt-dialog__confirm"
        [disabled]="!valid()"
        (click)="submit()"
      >
        {{ data.confirmLabel ?? 'OK' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    :host {
      display: block;
      min-width: 360px;
      max-width: 640px;
    }
    .r2m-text-prompt-dialog__field {
      width: 100%;
      margin-top: var(--r2m-space-1);
    }
  `,
})
export class TextPromptDialog {
  protected readonly data = inject<TextPromptOptions>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<TextPromptDialog, string | null>>(MatDialogRef);
  protected readonly value = signal(this.data.initial ?? '');
  protected readonly valid = computed(() => !this.data.required || this.value().trim().length > 0);

  protected submit(): void {
    if (this.valid()) this.ref.close(this.value().trim());
  }
}

@Injectable({ providedIn: 'root' })
export class PromptService {
  private readonly dialog = inject(MatDialog);

  async text(options: TextPromptOptions): Promise<string | null> {
    const ref = this.dialog.open<TextPromptDialog, TextPromptOptions, string | null>(
      TextPromptDialog,
      {
        data: options,
        autoFocus: 'first-tabbable',
        restoreFocus: true,
      },
    );
    return (await firstValueFrom(ref.afterClosed())) ?? null;
  }
}
