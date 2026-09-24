import { ChangeDetectionStrategy, Component, input, booleanAttribute } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Empty state for shelves, lists and the activity drawer (design §7): icon, headline, hint and an
 * optional primary action projected via `[action]`.
 */
@Component({
  selector: 'r2m-empty-state',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-empty-state', '[class.r2m-empty-state--compact]': 'compact()' },
  template: `
    <mat-icon class="r2m-empty-state__icon" aria-hidden="true">{{ icon() }}</mat-icon>
    <p class="r2m-empty-state__headline">{{ headline() }}</p>
    @if (hint()) {
      <p class="r2m-empty-state__hint">{{ hint() }}</p>
    }
    <div class="r2m-empty-state__action"><ng-content select="[action]" /></div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      align-items: center;
      text-align: center;
      padding: var(--r2m-space-10) var(--r2m-space-4);
      gap: var(--r2m-space-2);
      color: var(--r2m-text-muted);
    }
    :host(.r2m-empty-state--compact) {
      padding: var(--r2m-space-4);
    }
    .r2m-empty-state__icon {
      font-size: 48px;
      width: 48px;
      height: 48px;
      opacity: 0.7;
    }
    :host(.r2m-empty-state--compact) .r2m-empty-state__icon {
      font-size: 32px;
      width: 32px;
      height: 32px;
    }
    .r2m-empty-state__headline {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 500;
      color: var(--r2m-text);
    }
    .r2m-empty-state__hint {
      margin: 0;
      max-width: 40ch;
      font-size: var(--r2m-text-md);
    }
    .r2m-empty-state__action:empty {
      display: none;
    }
    .r2m-empty-state__action {
      margin-top: var(--r2m-space-2);
    }
  `,
})
export class EmptyState {
  readonly icon = input('inbox');
  readonly headline = input.required<string>();
  readonly hint = input<string>();
  /** Tighter padding for drawers and side panels. */
  readonly compact = input(false, { transform: booleanAttribute });
}
