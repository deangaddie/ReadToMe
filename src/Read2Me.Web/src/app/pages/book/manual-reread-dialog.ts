import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ManualImportRequest, SplitRuleMode } from '@app/api';
import {
  DEFAULT_MANUAL_REREAD_FORM,
  LevelKey,
  ManualRereadForm,
  SPLIT_MODES,
  activeLevels,
  toManualImportRequest,
  validateManualReread,
} from './manual-reread-form';

const LEVEL_HEADING: Record<LevelKey, string> = {
  volume: 'Volume detection',
  part: 'Part detection',
  chapter: 'Chapter detection',
};

const PREFIX_HINT: Record<LevelKey, string> = {
  volume: 'e.g. Volume, Book',
  part: 'e.g. Part',
  chapter: 'e.g. Chapter',
};

/**
 * Manual reread (research §3 ManualRereadDialog): structure switches, then one detection rule per
 * active level. Resolves the request to send, or nothing on Cancel. Validation is the pure form's.
 */
@Component({
  selector: 'app-manual-reread-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatRadioModule,
    MatSlideToggleModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Manual reread</h2>
    <mat-dialog-content class="manual">
      <p class="manual__hint">
        Re-splits the source file by the rules below. Existing chapters, attribution and audio are
        replaced.
      </p>

      <h3 class="manual__heading">Book structure</h3>
      <mat-slide-toggle
        class="manual__switch"
        [checked]="form().hasVolumes"
        (change)="patch({ hasVolumes: $event.checked })"
      >
        Book has multiple volumes
      </mat-slide-toggle>
      <mat-slide-toggle
        class="manual__switch"
        [checked]="form().hasParts"
        (change)="patch({ hasParts: $event.checked })"
      >
        {{ form().hasVolumes ? 'Each volume has multiple parts' : 'Book has multiple parts' }}
      </mat-slide-toggle>

      @for (level of levels(); track level) {
        <h3 class="manual__heading">{{ headings[level] }}</h3>
        <mat-radio-group
          class="manual__modes"
          [attr.aria-label]="headings[level]"
          [value]="form()[level].mode"
          (change)="setMode(level, $event.value)"
        >
          @for (m of modes; track m.mode) {
            <mat-radio-button [value]="m.mode">{{ m.label }}</mat-radio-button>
          }
        </mat-radio-group>
        @if (form()[level].mode === 'Prefix') {
          <mat-form-field class="manual__prefix" appearance="outline">
            <mat-label>Prefix ({{ hints[level] }})</mat-label>
            <input
              matInput
              type="text"
              [attr.data-level]="level"
              [value]="form()[level].prefix"
              (input)="setPrefix(level, $any($event.target).value)"
            />
          </mat-form-field>
        }
      }

      @if (error(); as message) {
        <p class="manual__error" role="alert">{{ message }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button mat-flat-button type="button" class="manual__submit" (click)="submit()">Reread</button>
    </mat-dialog-actions>
  `,
  styles: `
    :host {
      display: block;
      min-width: 420px;
      max-width: 560px;
    }
    .manual {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .manual__hint {
      margin: 0 0 var(--r2m-space-2);
      color: var(--r2m-text-muted);
    }
    .manual__heading {
      margin: var(--r2m-space-3) 0 0;
      font-size: var(--r2m-text-sm);
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--r2m-text-muted);
    }
    .manual__modes {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-2);
    }
    .manual__prefix {
      width: 100%;
    }
    .manual__error {
      margin: 0;
      color: var(--r2m-status-error);
    }
  `,
})
export class ManualRereadDialog {
  protected readonly ref = inject<MatDialogRef<ManualRereadDialog, ManualImportRequest>>(MatDialogRef);

  protected readonly form = signal<ManualRereadForm>({ ...DEFAULT_MANUAL_REREAD_FORM });
  /** Shown after the first failed submit, then live. */
  protected readonly submitted = signal(false);
  protected readonly levels = computed(() => activeLevels(this.form()));
  protected readonly error = computed(() =>
    this.submitted() ? validateManualReread(this.form()) : null,
  );

  protected readonly modes = SPLIT_MODES;
  protected readonly headings = LEVEL_HEADING;
  protected readonly hints = PREFIX_HINT;

  protected patch(changes: Partial<ManualRereadForm>): void {
    this.form.update((f) => ({ ...f, ...changes }));
  }

  protected setMode(level: LevelKey, mode: SplitRuleMode): void {
    this.form.update((f) => ({ ...f, [level]: { ...f[level], mode } }));
  }

  protected setPrefix(level: LevelKey, prefix: string): void {
    this.form.update((f) => ({ ...f, [level]: { ...f[level], prefix } }));
  }

  protected submit(): void {
    this.submitted.set(true);
    if (validateManualReread(this.form())) return;
    this.ref.close(toManualImportRequest(this.form()));
  }
}
