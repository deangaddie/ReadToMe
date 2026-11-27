import { afterEach, describe, expect, it } from 'bun:test';
import './inline-edit';
import { blockedWith } from '../../testing/description';

afterEach(() => document.body.replaceChildren());

async function mount() {
  const el = document.createElement('r2m-inline-edit');
  el.value = 'Chapter 1';
  el.placeholder = 'Title';
  el.required = true;
  const saved: string[] = [];
  let cancelled = 0;
  el.addEventListener('save', (e) => saved.push((e as CustomEvent<string>).detail));
  el.addEventListener('cancelled', () => cancelled++);
  document.body.append(el);
  await el.rendered();
  const display = () => el.querySelector<HTMLButtonElement>('.r2m-inline-edit__display');
  const field = () => el.querySelector<HTMLInputElement>('.r2m-inline-edit__input');
  const type = (value: string) => {
    const input = field()!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };
  return { el, saved, cancelled: () => cancelled, display, field, type };
}

describe('r2m-inline-edit', () => {
  it('shows the value and switches to an input on click', async () => {
    const { el, display, field } = await mount();
    expect(display()?.textContent).toContain('Chapter 1');

    display()!.click();
    await el.rendered();

    expect(field()?.value).toBe('Chapter 1');
    expect(el.classList.contains('r2m-inline-edit--editing')).toBe(true);
  });

  it('names its button with the text it shows, value or placeholder', async () => {
    const { el, display } = await mount();
    expect(display()?.getAttribute('aria-label')).toBe('Edit Title: Chapter 1');

    el.value = '';
    await el.rendered();

    expect(display()?.textContent?.trim()).toStartWith('Title');
    expect(display()?.getAttribute('aria-label')).toBe('Edit Title');
  });

  it('blocked, it shows the value with its button off and the reason, and never opens', async () => {
    const { el, display, field } = await mount();
    el.block = { reason: 'Viewing a past step' };
    await el.rendered();

    expect(display()?.textContent).toContain('Chapter 1');
    expect(blockedWith(display()!)).toBe('Viewing a past step');
    el.begin();
    await el.rendered();
    expect(field()).toBeNull();
  });

  it('saves a changed value on Enter', async () => {
    const { el, saved, display, field, type } = await mount();
    display()!.click();
    await el.rendered();

    type('Prologue');
    field()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await el.rendered();

    expect(saved).toEqual(['Prologue']);
    expect(field()).toBeNull();
  });

  it('cancels on Escape without emitting save', async () => {
    const { el, saved, cancelled, display, field, type } = await mount();
    display()!.click();
    await el.rendered();

    type('Changed');
    field()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await el.rendered();

    expect(saved).toEqual([]);
    expect(cancelled()).toBe(1);
    expect(display()?.textContent).toContain('Chapter 1');
  });

  it.each(['Enter', 'Escape'])('gives focus back to the display button after %s', async (key) => {
    const { el, display, field, type } = await mount();
    display()!.click();
    await el.rendered();

    type('Prologue');
    field()!.dispatchEvent(new KeyboardEvent('keydown', { key }));
    await el.rendered();

    expect(document.activeElement === display()).toBe(true);
  });

  it('leaves focus where the user moved it on a blur-save', async () => {
    const { el, saved, display, type } = await mount();
    const elsewhere = document.body.appendChild(document.createElement('button'));
    display()!.click();
    await el.rendered();

    type('Prologue');
    elsewhere.focus();
    await el.rendered();

    expect(saved).toEqual(['Prologue']);
    expect(document.activeElement === elsewhere).toBe(true);
  });

  it.each([
    { key: 'Enter', isComposing: true },
    { key: 'Escape', isComposing: true },
    // Safari ends the composition before the confirming keydown: only the key code still says so.
    { key: 'Enter', keyCode: 229 },
    { key: 'Escape', keyCode: 229 },
  ])('leaves $key to the IME while it composes', async (init) => {
    const { el, saved, cancelled, display, field, type } = await mount();
    display()!.click();
    await el.rendered();

    type('にほん');
    field()!.dispatchEvent(new KeyboardEvent('keydown', init));
    await el.rendered();

    expect(saved).toEqual([]);
    expect(cancelled()).toBe(0);
    expect(field()?.value).toBe('にほん');
  });

  it('does not save an unchanged or empty required value on blur', async () => {
    const { el, saved, cancelled, display, field, type } = await mount();
    display()!.click();
    await el.rendered();
    field()!.dispatchEvent(new Event('blur'));
    await el.rendered();
    expect(saved).toEqual([]);

    display()!.click();
    await el.rendered();
    type('   ');
    field()!.dispatchEvent(new Event('blur'));
    await el.rendered();
    expect(saved).toEqual([]);
    expect(cancelled()).toBe(2);
  });
});
