import { afterEach, describe, expect, it } from 'bun:test';
// Side-effect import registers the element; a type-only use alone would be elided.
import './speaker-menu';
import type { SpeakerMenu, SpeakerRosterEntry } from './speaker-menu';

/**
 * Ported from the Angular TestBed spec, case for case. No host component and no fixture: the
 * element is created, given properties, attached, and its events are plain DOM listeners.
 * `await menu.rendered()` stands in for `fixture.whenStable()`.
 */
const ROSTER: SpeakerRosterEntry[] = [
  { id: 'c-hardin', name: 'Hardin', aliases: ['Salvor'] },
  { id: 'c-pirenne', name: 'Pirenne' },
  { id: 'c-narr', name: 'Narrator', isNarrator: true },
  { id: 'c-anselm', name: 'Anselm' },
];

async function mount() {
  const menu = document.createElement('r2m-speaker-menu') as SpeakerMenu;
  menu.roster = ROSTER;
  menu.selectedId = 'c-pirenne';
  const events = { picked: [] as string[], cleared: 0, created: [] as string[] };
  menu.addEventListener('pick', (e) => events.picked.push((e as CustomEvent<string>).detail));
  menu.addEventListener('clear', () => events.cleared++);
  menu.addEventListener('create', (e) => events.created.push((e as CustomEvent<string>).detail));
  document.body.append(menu);
  await menu.rendered();
  const input = menu.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;
  const type = async (value: string) => {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await menu.rendered();
  };
  const press = (key: string, target: Element = input) =>
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
  return { menu, input, events, type, press };
}

const names = (el: Element) =>
  Array.from(el.querySelectorAll('.r2m-speaker-menu__name')).map((n) => n.textContent);

afterEach(() => document.body.replaceChildren());

describe('r2m-speaker-menu', () => {
  it('lists the narrator first, then the rest alphabetically, marking the selection', async () => {
    const { menu } = await mount();
    expect(names(menu)).toEqual(['Narrator', 'Anselm', 'Hardin', 'Pirenne']);
    expect(menu.querySelector('.r2m-speaker-menu__row--selected')?.textContent).toContain(
      'Pirenne',
    );
  });

  it('filters by name or alias, case-insensitively', async () => {
    const { menu, type } = await mount();
    await type('salv');
    expect(names(menu)).toEqual(['Hardin']);
    await type('zzz');
    expect(menu.querySelector('.r2m-speaker-menu__empty')?.textContent).toBe('No matches');
  });

  it('emits pick on row click, clear and create with the search text from the footer', async () => {
    const { menu, events, type } = await mount();
    menu.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row')[2]!.click();
    expect(events.picked).toEqual(['c-hardin']);

    await type('Gaal');
    const actions = menu.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
    actions[0]!.click();
    actions[1]!.click();
    expect(events.cleared).toBe(1);
    expect(events.created).toEqual(['Gaal']);
  });

  it('navigates with arrow keys and picks the active row on Enter', async () => {
    const { menu, events, press } = await mount();
    // Nothing is highlighted on open: the first ArrowDown lands on the narrator, the second on Anselm.
    press('ArrowDown', menu);
    press('ArrowDown', menu);
    press('Enter', menu);
    expect(events.picked).toEqual(['c-anselm']);
  });

  it('typing highlights the first match, and Enter picks it', async () => {
    const { menu, events, type, press } = await mount();
    await type('ha');
    expect(menu.querySelector('.r2m-speaker-menu__row--active')?.textContent).toContain('Hardin');
    expect(
      menu.querySelector('.r2m-speaker-menu__input')?.getAttribute('aria-activedescendant'),
    ).toBe(menu.querySelector('.r2m-speaker-menu__row--active')?.id);
    press('Enter');
    expect(events.picked).toEqual(['c-hardin']);
  });

  it('highlights while typing the way a browser sends keys (keydown before input)', async () => {
    const { menu, input, press } = await mount();
    press('n');
    input.value += 'n';
    input.dispatchEvent(new Event('input'));
    await menu.rendered();
    expect(menu.querySelector('.r2m-speaker-menu__row--active')?.textContent).toContain('Narrator');
  });

  it('Enter straight after typing picks the first match, before the list has re-rendered', async () => {
    const { input, events, press } = await mount();
    input.value = 'pir';
    input.dispatchEvent(new Event('input'));
    press('Enter'); // no await: the render has not happened yet
    expect(events.picked).toEqual(['c-pirenne']);
  });

  it('deleting the search back to empty drops the highlight, so Enter picks nobody', async () => {
    const { menu, events, type, press } = await mount();
    await type('ha');
    await type('');
    expect(menu.querySelector('.r2m-speaker-menu__row--active')).toBeNull();
    press('Enter');
    expect(events.picked).toEqual([]);
  });

  it('Enter on a search that matches nobody creates that character', async () => {
    const { events, type, press } = await mount();
    await type(' Gaal ');
    press('Enter');
    expect(events.created).toEqual(['Gaal']);
    expect(events.picked).toEqual([]);
  });

  it('Enter on a footer button is left to that button, even with a row highlighted', async () => {
    const { menu, events, type, press } = await mount();
    await type('ha');
    const clear = menu.querySelector<HTMLButtonElement>('.r2m-speaker-menu__action')!;
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    clear.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(events.picked).toEqual([]);
    press('Enter');
    expect(events.picked).toEqual(['c-hardin']);
  });

  it('a bare Enter in the empty search picks nobody', async () => {
    const { events, press } = await mount();
    press('Enter');
    expect(events.picked).toEqual([]);
    expect(events.created).toEqual([]);
  });
});
