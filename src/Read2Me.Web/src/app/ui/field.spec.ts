import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import { FakeTimers } from '../../testing/fake-timers';
import { ERROR_ANNOUNCE_DELAY_MS, type FieldControlAttrs, field } from './field';

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
    expect(input.getAttribute('aria-invalid')).toBe('false');
    expect(wrap.querySelector('.r2m-field__hint')).toBeNull();
    expect(wrap.querySelector('.r2m-field__error')).toBeNull();
  });

  it('describes the control by its hint', () => {
    const host = mount(field({ id: 'url', label: 'URL', hint: 'Absolute.' }, textInput));
    const hint = host.querySelector('.r2m-field__hint')!;
    expect(hint.id).toBe('url-hint');
    expect(hint.textContent).toBe('Absolute.');
    const input = host.querySelector('input')!;
    expect(input.getAttribute('aria-describedby')).toBe('url-hint');
    expect(input.getAttribute('aria-invalid')).toBe('false');
  });

  it('shows the error in place of the hint, not as a live region, and marks the control invalid', () => {
    const host = mount(
      field({ id: 'url', label: 'URL', hint: 'Absolute.', error: 'Required.' }, textInput),
    );
    expect(host.querySelector('.r2m-field__hint')).toBeNull();
    const error = host.querySelector('.r2m-field__error')!;
    expect(error.id).toBe('url-error');
    expect(error.hasAttribute('role')).toBe(false);
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

describe('field error announcement', () => {
  let timers: FakeTimers;
  let said: string[];
  let host: HTMLElement;

  beforeEach(() => {
    timers = new FakeTimers().install();
    said = [];
    document.ariaNotify = (text: string) => void said.push(text);
    host = document.createElement('div');
    document.body.append(host);
  });

  afterEach(() => {
    timers.restore();
    delete document.ariaNotify;
    document.body.replaceChildren();
  });

  /** Renders the Title field showing `error`, the way a form re-renders it. */
  const show = (error: string | null) =>
    render(field({ id: 'title', label: 'Title', error }, textInput), host);
  const input = () => host.querySelector('input')!;

  it('announces an error a blur brings up once, politely and a moment later', async () => {
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Title is required');

    await timers.advance(ERROR_ANNOUNCE_DELAY_MS - 1);
    expect(said).toEqual([]);
    await timers.advance(1);
    expect(said).toEqual(['Title is required']);

    show('Title is required');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toHaveLength(1);
  });

  it('announces an error a change brings up, even while the control keeps focus', async () => {
    show(null);
    input().focus();
    input().dispatchEvent(new Event('change', { bubbles: true }));
    show('Pick one');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual(['Title: Pick one']);
  });

  it('says nothing again when typing changes only the error text', async () => {
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Too long by 1');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);

    input().focus();
    show('Too long by 2');
    show('Too long by 3');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS * 2);
    expect(said).toEqual(['Title: Too long by 1']);
  });

  it('says nothing for an error that appears without the field being left (a submit, typing)', async () => {
    show(null);
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    show('Title is required');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual([]);
  });

  it('says nothing when the error clears before the moment passes', async () => {
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Title is required');
    show(null);
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual([]);
  });

  it('says nothing when focus is back on the field it left: focus reads the description', async () => {
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Title is required');
    input().focus();
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual([]);
  });

  it('announces an error that came back, on the next blur', async () => {
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Title is required');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    show(null);
    input().dispatchEvent(new Event('blur'));
    show('Title is required');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual(['Title is required', 'Title is required']);
  });
});
