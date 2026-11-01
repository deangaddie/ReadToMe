import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { RecentAudioSampleDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi } from '../../../../testing/fake-api';
import { settle } from '../../../../testing/fake-navigation';
import { SAMPLE_LIMIT, SamplePicker } from './sample-picker-dialog';

const RECENT = '/api/audio/samples/recent';

const alice: RecentAudioSampleDto = {
  itemId: 'item-1',
  folder: 'audio-book',
  text: 'Alice said hello.',
  characterName: 'Alice',
  voiceName: 'Alice Voice',
  projectTitle: 'Audio Settings Book',
};
const narration: RecentAudioSampleDto = {
  itemId: 'item-2',
  folder: 'audio-book',
  text: 'The door opened.',
  characterName: null,
  voiceName: null,
  projectTitle: 'Audio Settings Book',
};

let api: FakeApi;
let limits: (string | null)[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  limits = [];
  api.on('GET', RECENT, (_body, url) => {
    limits.push(url.searchParams.get('limit'));
    return [alice, narration];
  });
});
afterEach(() => document.body.replaceChildren());

/** Opens the picker the way the step preview does and waits for its rows. */
async function open() {
  const result = new SamplePicker().pick();
  await settle();
  const dialog = document.querySelector('r2m-sample-picker-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

describe('r2m-sample-picker-dialog', () => {
  it('lists the recent samples with their book title and character', async () => {
    const { dialog } = await open();

    expect(limits).toEqual([String(SAMPLE_LIMIT)]);
    const rows = Array.from(dialog.querySelectorAll('.sample-picker__row'));
    expect(rows.map((r) => r.getAttribute('data-item'))).toEqual(['item-1', 'item-2']);
    expect(rows[0]?.textContent).toContain('Alice said hello.');
    expect(rows[0]?.querySelector('.sample-picker__meta')?.textContent).toBe(
      'Alice · Alice Voice · Audio Settings Book',
    );
    expect(rows[1]?.querySelector('.sample-picker__meta')?.textContent).toBe(
      'Narration · Audio Settings Book',
    );
  });

  it('resolves with the clicked sample', async () => {
    const { result, dialog } = await open();

    dialog.querySelector<HTMLButtonElement>('[data-item="item-2"]')!.click();

    expect(await result).toEqual(narration);
  });

  it('resolves null on Cancel', async () => {
    const { result, dialog } = await open();

    dialog.querySelector<HTMLButtonElement>('.sample-picker__cancel')!.click();

    expect(await result).toBeNull();
  });
});
