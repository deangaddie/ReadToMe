import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  booleanAttribute,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltip } from '@angular/material/tooltip';

export type CountBadgeKind = 'attribution' | 'audio' | 'review';

const DEFAULT_ICONS: Record<CountBadgeKind, string> = {
  attribution: 'person_search',
  audio: 'graphic_eq',
  review: 'rate_review',
};

/**
 * Count badge (design §7): number + icon coloured by kind, for tree nodes and the pipeline
 * stepper. Hides itself at zero unless `showZero`. `title` (tooltip) comes from the MatTooltip
 * host directive.
 */
@Component({
  selector: 'r2m-count-badge',
  imports: [MatIconModule],
  hostDirectives: [{ directive: MatTooltip, inputs: ['matTooltip: title'] }],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-count-badge',
    '[class]': '"r2m-count-badge--" + kind()',
    '[hidden]': 'hidden()',
  },
  template: `
    <mat-icon class="r2m-count-badge__icon" aria-hidden="true">{{ resolvedIcon() }}</mat-icon>
    <span class="r2m-count-badge__count">{{ count() }}</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      height: 18px;
      padding: 0 6px 0 4px;
      border-radius: var(--r2m-radius-pill);
      font-size: var(--r2m-text-xs);
      font-weight: 600;
      line-height: 1;
      white-space: nowrap;
      color: var(--_fg);
      background: color-mix(in srgb, var(--_fg) 14%, transparent);
      --_fg: var(--r2m-text-muted);
    }
    :host(.r2m-count-badge--attribution) {
      --_fg: var(--r2m-badge-attribution);
    }
    :host(.r2m-count-badge--audio) {
      --_fg: var(--r2m-badge-audio);
    }
    :host(.r2m-count-badge--review) {
      --_fg: var(--r2m-badge-review);
    }
    .r2m-count-badge__icon {
      font-size: 14px;
      width: 14px;
      height: 14px;
      color: inherit;
    }
  `,
})
export class CountBadge {
  readonly count = input.required<number>();
  readonly kind = input<CountBadgeKind>('attribution');
  /** Material Symbols name; defaults per kind. `title` is the MatTooltip host-directive input. */
  readonly icon = input<string>();
  readonly showZero = input(false, { transform: booleanAttribute });

  protected readonly resolvedIcon = computed(() => this.icon() ?? DEFAULT_ICONS[this.kind()]);
  protected readonly hidden = computed(() => this.count() === 0 && !this.showZero());
}
