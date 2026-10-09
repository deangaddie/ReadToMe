import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, NarratorDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { settle } from '../../../testing/fake-navigation';
import type { NarratorBanner } from './narrator-banner';
import { unlinkWarning } from './narrator-banner';
import './narrator-banner';

function row(name: string, readyVoiceCount = 0): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount: readyVoiceCount,
    readyVoiceCount,
    isNarrator: name === 'Narrator',
    narratesBook: false,
  };
}

const UNLINKED: NarratorDto = { characterId: 'narrator', displayName: 'Narrator', isLinked: false };
const LINKED: NarratorDto = { characterId: 'watson', displayName: 'Watson', isLinked: true };
const ROWS = [row('Narrator'), row('Watson', 1), row('Lestrade')];

beforeEach(() => resetServices());
afterEach(() => document.body.replaceChildren());

async function mount(narrator: NarratorDto, rows = ROWS, busy = false) {
  const banner = document.createElement('r2m-narrator-banner');
  banner.narrator = narrator;
  banner.rows = rows;
  banner.busy = busy;
  const changes: (string | null)[] = [];
  banner.addEventListener('narrator-changed', (e) =>
    changes.push((e as CustomEvent<string | null>).detail),
  );
  document.body.append(banner);
  await banner.rendered();
  return { banner, changes };
}

const select = (banner: NarratorBanner) => banner.querySelector<HTMLSelectElement>('select')!;
const options = (banner: NarratorBanner) =>
  Array.from(banner.querySelectorAll<HTMLOptionElement>('option:not([disabled])')).map((o) =>
    o.textContent?.trim(),
  );

describe('r2m-narrator-banner', () => {
  it('unlinked: invites a pick from every character but the Narrator', async () => {
    const { banner } = await mount(UNLINKED);
    expect(banner.textContent).toContain('First-person book? Say who tells it');
    expect(options(banner)).toEqual(['Watson', 'Lestrade']);
    expect(select(banner).disabled).toBe(false);
  });

  it('the picker is off while busy or without any candidate', async () => {
    const busy = await mount(UNLINKED, ROWS, true);
    expect(select(busy.banner).disabled).toBe(true);
    document.body.replaceChildren();
    const empty = await mount(UNLINKED, [row('Narrator')]);
    expect(select(empty.banner).disabled).toBe(true);
  });

  it('a pick emits the character id', async () => {
    const { banner, changes } = await mount(UNLINKED);
    const picker = select(banner);
    picker.value = 'watson';
    picker.dispatchEvent(new Event('change'));
    expect(changes).toEqual(['watson']);
  });

  it('linked: names the narrator, counts its ready voices, offers Change and Unlink', async () => {
    const { banner } = await mount(LINKED);
    expect(banner.textContent).toContain('Narrated by');
    expect(banner.querySelector('strong')?.textContent).toBe('Watson');
    expect(banner.querySelector('.r2m-status-chip')?.textContent).toContain('1 ready voice');
    expect(banner.querySelector('select')).toBeNull();
    expect(banner.querySelector('.narrator-banner__unlink')).not.toBeNull();
  });

  it('no ready voices warns', async () => {
    const { banner } = await mount(LINKED, [row('Narrator'), row('Watson', 0)]);
    const chip = banner.querySelector('.r2m-status-chip')!;
    expect(chip.classList).toContain('r2m-status-chip--warn');
    expect(chip.textContent).toContain('0 ready voices');
  });

  it('Change shows the picker again, and Cancel hides it; a pick emits and closes it', async () => {
    const { banner, changes } = await mount(LINKED);
    banner.querySelector<HTMLButtonElement>('.r2m-button')!.click();
    await banner.rendered();
    expect(options(banner)).toEqual(['Watson', 'Lestrade']);

    const cancel = Array.from(banner.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Cancel',
    )!;
    cancel.click();
    await banner.rendered();
    expect(banner.querySelector('select')).toBeNull();

    banner.changing.set(true);
    await banner.rendered();
    const picker = select(banner);
    picker.value = 'lestrade';
    picker.dispatchEvent(new Event('change'));
    await banner.rendered();
    expect(changes).toEqual(['lestrade']);
    expect(banner.changing()).toBe(false);
  });

  it('Unlink asks first and emits null only when confirmed', async () => {
    const { banner, changes } = await mount(LINKED);
    banner.querySelector<HTMLButtonElement>('.narrator-banner__unlink')!.click();
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    await dialog.rendered();
    expect(dialog.textContent).toContain(unlinkWarning('Watson'));
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__cancel')!.click();
    await settle();
    expect(changes).toEqual([]);

    banner.querySelector<HTMLButtonElement>('.narrator-banner__unlink')!.click();
    await settle();
    document
      .querySelector('r2m-confirm-dialog')!
      .querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!
      .click();
    await settle();
    expect(changes).toEqual([null]);
  });
});
