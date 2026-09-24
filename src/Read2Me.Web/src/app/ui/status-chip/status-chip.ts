import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  booleanAttribute,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltip } from '@angular/material/tooltip';

export type StatusKind = 'ok' | 'warn' | 'error' | 'info' | 'busy' | 'neutral';

const DEFAULT_ICONS: Record<StatusKind, string> = {
  ok: 'check_circle',
  warn: 'warning',
  error: 'error',
  info: 'info',
  busy: 'progress_activity',
  neutral: 'radio_button_unchecked',
};

/**
 * Status chip (design §7): status conveyed by colour *and* icon *and* text, never colour alone.
 * `busy` swaps the icon for a 14 px spinner. Replaces every ad-hoc Material chip in the app; the
 * ESLint restricted-imports rule keeps raw `mat-chip` inside `app/ui/`.
 */
@Component({
  selector: 'r2m-status-chip',
  imports: [MatIconModule, MatProgressSpinnerModule],
  hostDirectives: [{ directive: MatTooltip, inputs: ['matTooltip: tooltip'] }],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-status-chip',
    '[class]': '"r2m-status-chip--" + status()',
    '[class.r2m-status-chip--compact]': 'compact()',
    '[attr.role]': '"status"',
  },
  template: `
    @if (status() === 'busy') {
      <mat-progress-spinner class="r2m-status-chip__spinner" mode="indeterminate" diameter="14" />
    } @else {
      <mat-icon class="r2m-status-chip__icon" aria-hidden="true">{{ resolvedIcon() }}</mat-icon>
    }
    <span class="r2m-status-chip__label">{{ label() }}</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-1);
      height: 24px;
      padding: 0 var(--r2m-space-2) 0 6px;
      border-radius: var(--r2m-radius-pill);
      font-size: var(--r2m-text-sm);
      font-weight: 500;
      line-height: 1;
      white-space: nowrap;
      color: var(--_fg);
      background: var(--_bg);
      --_fg: var(--r2m-status-neutral);
      --_bg: var(--r2m-status-neutral-soft);
    }
    :host(.r2m-status-chip--compact) {
      height: 20px;
      font-size: var(--r2m-text-xs);
      padding-right: 6px;
    }
    :host(.r2m-status-chip--ok) {
      --_fg: var(--r2m-status-ok);
      --_bg: var(--r2m-status-ok-soft);
    }
    :host(.r2m-status-chip--warn) {
      --_fg: var(--r2m-status-warn);
      --_bg: var(--r2m-status-warn-soft);
    }
    :host(.r2m-status-chip--error) {
      --_fg: var(--r2m-status-error);
      --_bg: var(--r2m-status-error-soft);
    }
    :host(.r2m-status-chip--info) {
      --_fg: var(--r2m-status-info);
      --_bg: var(--r2m-status-info-soft);
    }
    :host(.r2m-status-chip--busy) {
      --_fg: var(--r2m-status-busy);
      --_bg: var(--r2m-status-busy-soft);
    }
    .r2m-status-chip__icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: inherit;
    }
    .r2m-status-chip__spinner {
      --mat-progress-spinner-active-indicator-color: var(--_fg);
    }
  `,
})
export class StatusChip {
  readonly status = input<StatusKind>('neutral');
  readonly label = input.required<string>();
  /** `tooltip` is provided by the MatTooltip host directive. Material Symbols name; defaults per status. */
  readonly icon = input<string>();
  readonly compact = input(false, { transform: booleanAttribute });

  protected readonly resolvedIcon = computed(() => this.icon() ?? DEFAULT_ICONS[this.status()]);
}
