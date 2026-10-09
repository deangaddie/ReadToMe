import { html } from 'lit-html';

export interface KeyValueRow {
  label: string;
  value: string | number | null | undefined;
  /** Render the value in the monospace stack (ids, paths, JSON). */
  mono?: boolean;
}

/** Label/value rows for overviews and detail panels (design §7). Missing values show an em dash. */
export function keyValue(rows: readonly KeyValueRow[], options: { dense?: boolean } = {}) {
  return html`<dl class="r2m-key-value ${options.dense ? 'r2m-key-value--dense' : ''}">
    ${rows.map((row) => {
      const empty = row.value === null || row.value === undefined || row.value === '';
      return html`<dt class="r2m-key-value__label">${row.label}</dt>
        ${
          empty
            ? html`<dd class="r2m-key-value__value r2m-key-value__value--empty">—</dd>`
            : html`<dd
                class="r2m-key-value__value ${row.mono ? 'r2m-key-value__value--mono' : ''}"
              >
                ${row.value}
              </dd>`
        }`;
    })}
  </dl>`;
}
