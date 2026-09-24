import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * Page header used by every page (design §7): title, optional subtitle, and two content slots —
 * `[breadcrumb]` above the title and `[actions]` on the right.
 */
@Component({
  selector: 'r2m-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-page-header' },
  template: `
    <div class="r2m-page-header__breadcrumb"><ng-content select="[breadcrumb]" /></div>
    <div class="r2m-page-header__row">
      <div class="r2m-page-header__text">
        <h1 class="r2m-page-header__title">{{ title() }}</h1>
        @if (subtitle()) {
          <p class="r2m-page-header__subtitle">{{ subtitle() }}</p>
        }
      </div>
      <div class="r2m-page-header__actions"><ng-content select="[actions]" /></div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-4) 0 var(--r2m-space-3);
    }
    .r2m-page-header__breadcrumb:empty {
      display: none;
    }
    .r2m-page-header__breadcrumb {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
      margin-bottom: var(--r2m-space-1);
    }
    .r2m-page-header__row {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--r2m-space-4);
      flex-wrap: wrap;
    }
    .r2m-page-header__title {
      margin: 0;
      font-size: var(--r2m-text-2xl);
      font-weight: 600;
      line-height: var(--r2m-line-tight);
      color: var(--r2m-text);
    }
    .r2m-page-header__subtitle {
      margin: var(--r2m-space-1) 0 0;
      font-size: var(--r2m-text-md);
      color: var(--r2m-text-muted);
    }
    .r2m-page-header__actions {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .r2m-page-header__actions:empty {
      display: none;
    }
  `,
})
export class PageHeader {
  readonly title = input.required<string>();
  readonly subtitle = input<string>();
}
