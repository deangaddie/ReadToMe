import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { type PronunciationDto, VIEWING_READ_ONLY } from '@app/api';
import { ActivityStore } from '@app/activity/activity-store';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ConfirmService } from '@app/ui/dialogs';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { markedText } from './pronunciation-marks';
import { PronunciationPanelState } from './pronunciation-panel-store';
import type { PronunciationPill } from './pronunciation-pill';
import './pronunciation-pill';

const PUG: PronunciationDto = {
  id: 'pug',
  writtenForm: 'Pug',
  spokenForm: 'PUHG',
  matchCount: 1,
  itemCount: 1,
  withAudioCount: 0,
};

let host: HTMLElement;
let state: PronunciationPanelState;
let pill: PronunciationPill;

beforeEach(async () => {
  resetServices();
  new FakeApi().install();
  new FakeLive().install();
  override(ActivityStore, {
    drawerOpen: signal(false),
    closeDrawer: () => undefined,
  } as unknown as ActivityStore);
  override(ConfirmService, {
    confirm: async () => true,
  } as unknown as ConfirmService);
  state = new PronunciationPanelState(() => false);
  state.entries.set([PUG]);

  host = document.createElement('div');
  const text = document.createElement('span');
  text.dataset['itemText'] = 'i1';
  render(markedText('i1', 'Then Pug met Kulgan.', []), text);
  host.append(text);
  pill = document.createElement('r2m-pronunciation-pill') as PronunciationPill;
  pill.state = state;
  pill.readOnly = null;
  host.append(pill);
  document.body.append(host);
  await pill.rendered();
});

afterEach(() => {
  document.getSelection()?.removeAllRanges();
  host.remove();
  state.dispose();
});

/** Selects `text` in the item text, as a drag would, and lets the pill read it. */
async function select(text: string) {
  const node = [...host.querySelector('[data-item-text]')!.childNodes].find(
    (n): n is Text => n instanceof Text && n.data.includes(text),
  )!;
  const range = document.createRange();
  range.setStart(node, node.data.indexOf(text));
  range.setEnd(node, node.data.indexOf(text) + text.length);
  const selection = document.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(range);
  pill.readSelection();
  await pill.rendered();
}

const button = () => pill.querySelector<HTMLButtonElement>('[data-action=pill]');

describe('the selection pill', () => {
  it('offers Add pronunciation for a selected word, and opens the panel editor pre-filled with it', async () => {
    await select('Kulgan');
    expect(button()!.textContent).toContain('Add pronunciation');

    button()!.click();
    await Promise.resolve();

    expect(state.open()).toBe(true);
    expect(state.session()).toMatchObject({
      entryId: null,
      forms: { writtenForm: 'Kulgan', spokenForm: 'Kulgan' },
      origin: { itemId: 'i1', offset: 13 },
    });
    expect(button()).toBeNull();
  });

  it('offers Edit pronunciation when the selection is already a written form', async () => {
    await select('Pug');
    expect(button()!.textContent).toContain('Edit pronunciation');
    button()!.click();
    await Promise.resolve();
    expect(state.session()).toMatchObject({
      entryId: 'pug',
      origin: { itemId: 'i1', offset: 5 },
    });
  });

  it('offers nothing while a step is viewed', async () => {
    pill.readOnly = VIEWING_READ_ONLY;
    await select('Kulgan');
    expect(button()).toBeNull();
  });
});
