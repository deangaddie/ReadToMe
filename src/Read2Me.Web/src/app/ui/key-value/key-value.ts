import { ChangeDetectionStrategy, Component, input, booleanAttribute } from '@angular/core';

export interface KeyValueRow {
  label: string;
  value: string | number | null | undefined;
  /** Render the value in the monospace stack (ids, paths, JSON). */
  mono?: boolean;
}

/** Label/value rows for overviews and detail panels (design §7). Missing values show an em dash. */
@Component({
  selector: 'r2m-key-value',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-key-value', '[class.r2m-key-value--dense]': 'dense()' },
  template: `
    <dl class="r2m-key-value__list">
      @for (row of rows(); track row.label) {
        <dt class="r2m-key-value__label">{{ row.label }}</dt>
        @if (row.value === null || row.value === undefined || row.value === '') {
          <dd class="r2m-key-value__value r2m-key-value__value--empty">—</dd>
        } @else {
          <dd class="r2m-key-value__value" [class.r2m-key-value__value--mono]="row.mono">
            {{ row.value }}
          </dd>
        }
      }
    </dl>
  `,
  styles: `
    :host {
      display: block;
    }
    .r2m-key-value__list {
      display: grid;
      grid-template-columns: max-content 1fr;
      column-gap: var(--r2m-space-4);
      row-gap: var(--r2m-space-2);
      margin: 0;
      font-size: var(--r2m-text-md);
    }
    :host(.r2m-key-value--dense) .r2m-key-value__list {
      row-gap: var(--r2m-space-1);
      font-size: var(--r2m-text-sm);
    }
    .r2m-key-value__label {
      color: var(--r2m-text-muted);
    }
    .r2m-key-value__value {
      margin: 0;
      color: var(--r2m-text);
      overflow-wrap: anywhere;
    }
    .r2m-key-value__value--empty {
      color: var(--r2m-text-muted);
    }
    .r2m-key-value__value--mono {
      font-family: var(--r2m-font-mono);
      font-size: var(--r2m-text-sm);
    }
  `,
})
export class KeyValue {
  readonly rows = input.required<KeyValueRow[]>();
  readonly dense = input(false, { transform: booleanAttribute });
}
