import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import { type FieldControlAttrs, field } from './field';

function mount(template: unknown): HTMLElement {
  const host = document.createElement('div');
  render(template, host);
  return host;
}

const textInput = (a: FieldControlAttrs) =>
  html`<input id=${a.id} aria-describedby=${a.describedBy} aria-invalid=${a.invalid} />`;

describe('field', () => {
  it('stacks a label over the control and ties them by id', () => {
    const host = mount(field({ id: 'name', label: 'Name' }, textInput));
    const wrap = host.querySelector('.r2m-field')!;
    expect(wrap.classList.contains('r2m-field--inline')).toBe(false);
    const label = wrap.querySelector<HTMLLabelElement>('label.r2m-field__label')!;
    expect(label.textContent).toBe('Name');
    expect(label.getAttribute('for')).toBe('name');
    const input = wrap.querySelector('input')!;
    expect(input.id).toBe('name');
    expect(input.hasAttribute('aria-describedby')).toBe(false);
    expect(input.hasAttribute('aria-invalid')).toBe(false);
    expect(wrap.querySelector('.r2m-field__hint')).toBeNull();
    expect(wrap.querySelector('.r2m-field__error')).toBeNull();
  });

  it('describes the control by its hint', () => {
    const host = mount(field({ id: 'url', label: 'URL', hint: 'Absolute.' }, textInput));
    const hint = host.querySelector('.r2m-field__hint')!;
    expect(hint.id).toBe('url-hint');
    expect(hint.textContent).toBe('Absolute.');
    expect(host.querySelector('input')?.getAttribute('aria-describedby')).toBe('url-hint');
  });

  it('shows the error in place of the hint and marks the control invalid', () => {
    const host = mount(
      field({ id: 'url', label: 'URL', hint: 'Absolute.', error: 'Required.' }, textInput),
    );
    expect(host.querySelector('.r2m-field__hint')).toBeNull();
    const error = host.querySelector('.r2m-field__error')!;
    expect(error.id).toBe('url-error');
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toBe('Required.');
    const input = host.querySelector('input')!;
    expect(input.getAttribute('aria-describedby')).toBe('url-error');
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });

  it('takes the inline form and extra classes', () => {
    const host = mount(field({ id: 'q', label: 'Search', inline: true, cls: 'extra' }, textInput));
    const wrap = host.querySelector('.r2m-field')!;
    expect(wrap.classList.contains('r2m-field--inline')).toBe(true);
    expect(wrap.classList.contains('extra')).toBe(true);
  });
});
