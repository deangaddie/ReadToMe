import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  output,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

export const DIRTY_GUARD_MESSAGE = 'You have unsaved changes. Leave and discard them?';

/** Message for a CanDeactivate guard (wired in a later ticket), or null when nothing would be lost. */
export function dirtyGuardMessage(dirty: boolean): string | null {
  return dirty ? DIRTY_GUARD_MESSAGE : null;
}

/**
 * Detail-side frame of the settings master–detail template (design §6.4, §7): title, projected
 * form body, and a Save / Cancel footer gated on the dirty flag.
 */
@Component({
  selector: 'r2m-config-editor-frame',
  imports: [MatButtonModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-config-editor-frame' },
  template: `
    <header class="r2m-config-editor-frame__head">
      <div>
        <h2 class="r2m-config-editor-frame__title">{{ title() }}</h2>
        @if (subtitle()) {
          <p class="r2m-config-editor-frame__subtitle">{{ subtitle() }}</p>
        }
      </div>
      @if (dirty()) {
        <span class="r2m-config-editor-frame__dirty" role="status">Unsaved changes</span>
      }
    </header>
    <div class="r2m-config-editor-frame__body"><ng-content /></div>
    <footer class="r2m-config-editor-frame__foot">
      <button mat-button type="button" [disabled]="!dirty() || saving()" (click)="cancel.emit()">
        Cancel
      </button>
      <button mat-flat-button type="button" [disabled]="!saveEnabled()" (click)="save.emit()">
        @if (saving()) {
          <mat-progress-spinner mode="indeterminate" diameter="16" />
        }
        {{ saving() ? 'Saving…' : 'Save' }}
      </button>
    </footer>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .r2m-config-editor-frame__head {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-4) var(--r2m-space-4) var(--r2m-space-3);
      border-bottom: 1px solid var(--r2m-outline);
    }
    .r2m-config-editor-frame__title {
      margin: 0;
      font-size: var(--r2m-text-xl);
      font-weight: 600;
    }
    .r2m-config-editor-frame__subtitle {
      margin: var(--r2m-space-1) 0 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-config-editor-frame__dirty {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-warn);
    }
    .r2m-config-editor-frame__body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      padding: var(--r2m-space-4);
    }
    .r2m-config-editor-frame__foot {
      display: flex;
      justify-content: flex-end;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-3) var(--r2m-space-4);
      border-top: 1px solid var(--r2m-outline);
    }
    mat-progress-spinner {
      display: inline-block;
      margin-right: var(--r2m-space-2);
      vertical-align: middle;
    }
  `,
})
export class ConfigEditorFrame {
  readonly title = input.required<string>();
  readonly subtitle = input<string>();
  readonly dirty = input(false, { transform: booleanAttribute });
  readonly saving = input(false, { transform: booleanAttribute });
  readonly canSave = input(true, { transform: booleanAttribute });

  readonly save = output<void>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly cancel = output<void>();

  protected readonly saveEnabled = computed(() => this.dirty() && this.canSave() && !this.saving());
}
