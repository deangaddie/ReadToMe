import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { type KeyValueRow, keyValue } from './key-value';

const ROWS: KeyValueRow[] = [
  { label: 'Title', value: 'Foundation' },
  { label: 'Folder', value: 'foundation', mono: true },
  { label: 'Narrator', value: null },
  { label: 'Items', value: 3415 },
];

afterEach(() => document.body.replaceChildren());

describe('keyValue()', () => {
  it('renders label/value pairs, mono values and em dashes for missing values', () => {
    const el = document.createElement('div');
    document.body.append(el);
    render(keyValue(ROWS, { dense: true }), el);

    const labels = Array.from(el.querySelectorAll('dt')).map((n) => n.textContent?.trim());
    const values = Array.from(el.querySelectorAll('dd')).map((n) => n.textContent?.trim());
    expect(labels).toEqual(['Title', 'Folder', 'Narrator', 'Items']);
    expect(values).toEqual(['Foundation', 'foundation', '—', '3415']);
    expect(el.querySelectorAll('dd')[1]?.classList.contains('r2m-key-value__value--mono')).toBe(
      true,
    );
    expect(el.querySelectorAll('dd')[2]?.classList.contains('r2m-key-value__value--empty')).toBe(
      true,
    );
    expect(el.querySelector('.r2m-key-value')?.classList.contains('r2m-key-value--dense')).toBe(
      true,
    );
  });
});
