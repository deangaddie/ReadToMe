import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { speakerHue } from '@app/shared/speaker-color';
// Side-effect import registers the element; a type-only use alone would be elided.
import './speaker-chip';
import type { SpeakerChip } from './speaker-chip';
import type { SpeakerRosterEntry } from './speaker-menu';

const ROSTER: SpeakerRosterEntry[] = [
  { id: 'c-hardin', name: 'Hardin' },
  { id: 'c-narr', name: 'Narrator', isNarrator: true },
];

// happy-dom has no popover: opening is simulated with the `toggle` event the browser would fire,
// and light dismiss, the top layer and anchor positioning are covered in E2E.
const proto = HTMLElement.prototype as unknown as Record<string, () => void>;
let hidden = 0;
beforeEach(() => {
  hidden = 0;
  proto['hidePopover'] = () => void hidden++;
});
afterEach(() => {
  delete proto['hidePopover'];
  document.body.replaceChildren();
});

async function mount(props: Partial<SpeakerChip> = {}): Promise<SpeakerChip> {
  const chip = Object.assign(document.createElement('r2m-speaker-chip'), props);
  document.body.append(chip);
  await chip.rendered();
  return chip;
}

async function open(chip: SpeakerChip): Promise<void> {
  const popover = chip.querySelector('.r2m-speaker-chip__menu')!;
  popover.dispatchEvent(Object.assign(new Event('toggle'), { newState: 'open' }));
  await chip.rendered();
}

const label = (chip: Element) => chip.querySelector('.r2m-speaker-chip__name')?.textContent;

describe('r2m-speaker-chip', () => {
  it('shows the name with a dot in the character hue', async () => {
    const chip = await mount({ name: 'Hardin', characterId: 'c-hardin' });
    expect(label(chip)).toBe('Hardin');
    expect(chip.classList.contains('r2m-speaker-chip')).toBe(true);
    expect(chip.classList.contains('r2m-speaker-chip--named')).toBe(true);
    expect(chip.querySelector('.r2m-speaker-chip__dot')).not.toBeNull();
    expect(chip.style.getPropertyValue('--r2m-speaker-hue')).toBe(String(speakerHue('c-hardin')));
  });

  it('falls back to the name for the hue when there is no character id', async () => {
    const chip = await mount({ name: 'Hardin' });
    expect(chip.style.getPropertyValue('--r2m-speaker-hue')).toBe(String(speakerHue('Hardin')));
  });

  it.each([
    ['unknown', 'Unknown', 'question_mark'],
    ['mixed', 'Mixed', 'call_split'],
    ['narration', 'Narration', 'auto_stories'],
  ] as const)('the %s state has its own label and icon', async (state, text, glyph) => {
    const chip = await mount({ state });
    expect(label(chip)).toBe(text);
    expect(chip.querySelector('.r2m-speaker-chip__icon')?.textContent).toBe(glyph);
    expect(chip.classList.contains(`r2m-speaker-chip--${state}`)).toBe(true);
    expect(chip.classList.contains('r2m-speaker-chip--named')).toBe(false);
  });

  it('a state keeps a given name over its default label', async () => {
    const chip = await mount({ state: 'narration', name: 'The Encyclopedist' });
    expect(label(chip)).toBe('The Encyclopedist');
  });

  it('marks a linked narrator', async () => {
    const chip = await mount({ state: 'narrator-linked', name: 'Hardin' });
    expect(chip.querySelector('.r2m-speaker-chip__link')?.getAttribute('aria-label')).toBe(
      'Linked narrator',
    );
  });

  it('follows the compact input', async () => {
    const chip = await mount({ name: 'Hardin', compact: true });
    expect(chip.classList.contains('r2m-speaker-chip--compact')).toBe(true);
    chip.compact = false;
    expect(chip.classList.contains('r2m-speaker-chip--compact')).toBe(false);
  });

  it('without a roster it is plain text, not a button', async () => {
    const chip = await mount({ name: 'Hardin' });
    expect(chip.querySelector('button')).toBeNull();
    expect(chip.querySelector('[popover]')).toBeNull();
    expect(chip.classList.contains('r2m-speaker-chip--interactive')).toBe(false);
  });

  it('with a roster it is a button that targets its menu popover', async () => {
    const chip = await mount({ name: 'Hardin', roster: ROSTER });
    const button = chip.querySelector('button.r2m-speaker-chip__body')!;
    const popover = chip.querySelector('.r2m-speaker-chip__menu')!;
    expect(button.getAttribute('popovertarget')).toBe(popover.id);
    expect(popover.hasAttribute('popover')).toBe(true);
    expect(chip.classList.contains('r2m-speaker-chip--interactive')).toBe(true);
  });

  it('two chips get different menu ids', async () => {
    const a = await mount({ name: 'a', roster: ROSTER });
    const b = await mount({ name: 'b', roster: ROSTER });
    expect(a.querySelector('[popover]')?.id).not.toBe(b.querySelector('[popover]')?.id);
  });

  it('renders the menu only while open, so each open starts fresh', async () => {
    const chip = await mount({ name: 'Hardin', characterId: 'c-hardin', roster: ROSTER });
    expect(chip.querySelector('r2m-speaker-menu')).toBeNull();
    await open(chip);
    const menu = chip.querySelector('r2m-speaker-menu')!;
    expect(menu.roster).toBe(ROSTER);
    expect(menu.selectedId).toBe('c-hardin');

    chip
      .querySelector('.r2m-speaker-chip__menu')!
      .dispatchEvent(Object.assign(new Event('toggle'), { newState: 'closed' }));
    await chip.rendered();
    expect(chip.querySelector('r2m-speaker-menu')).toBeNull();
  });

  it("re-exposes the menu's pick, clear and create events and closes the menu", async () => {
    const chip = await mount({ name: 'Hardin', roster: ROSTER });
    const seen: string[] = [];
    for (const type of ['pick', 'clear', 'create']) {
      chip.addEventListener(type, (e) => seen.push(`${type}:${(e as CustomEvent).detail ?? ''}`));
    }
    await open(chip);
    const menu = chip.querySelector('r2m-speaker-menu')!;
    await menu.rendered();
    menu.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row')[1]!.click();
    const actions = menu.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
    actions[0]!.click();
    actions[1]!.click();
    expect(seen).toEqual(['pick:c-hardin', 'clear:', 'create:']);
    expect(hidden).toBe(3);
  });
});
