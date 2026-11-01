import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { PronunciationEntry } from '@app/api';
import { ActivityStore } from '@app/activity/activity-store';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { Receipt } from '@app/live/live-messages';
import { ConfirmService } from '@app/ui/dialogs';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { PronunciationPanelState } from './pronunciation-panel-store';

const LIST = '/api/projects/the-adventures-of-sherlock-holmes/pronunciations';
const HOLMES: PronunciationEntry = {
  id: 'holmes',
  writtenForm: 'Holmes',
  spokenForm: 'HOHMZ',
  matchCount: 3,
  itemCount: 2,
  withAudioCount: 0,
};

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

let api: FakeApi;
let live: FakeLive;
let drawerOpen: ReturnType<typeof signal<boolean>>;
let dirty: boolean;
let confirms: number;
let confirmAnswer: boolean;
let state: PronunciationPanelState;
let list: PronunciationEntry[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  list = [HOLMES];
  api.on('GET', LIST, () => Response.json(list));
  live = new FakeLive().install();
  drawerOpen = signal(false);
  override(ActivityStore, {
    drawerOpen,
    openDrawer: () => drawerOpen.set(true),
    closeDrawer: () => drawerOpen.set(false),
  } as unknown as ActivityStore);
  dirty = false;
  confirms = 0;
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async () => {
      confirms++;
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  state = new PronunciationPanelState(() => dirty);
});

afterEach(() => state.dispose());

describe('the Pronunciations panel and the Activity drawer', () => {
  it('share the right-hand space: opening the panel closes the drawer', () => {
    drawerOpen.set(true);
    state.show();
    expect(state.open()).toBe(true);
    expect(drawerOpen()).toBe(false);
  });

  it('share the right-hand space: opening the drawer closes the panel, which keeps its draft to come back to', async () => {
    await state.add({ writtenForm: 'Watson', spokenForm: 'Watson' }, { itemId: 'i1', offset: 4 });
    const session = state.session();

    drawerOpen.set(true);
    expect(state.open()).toBe(false);

    state.show();
    expect(drawerOpen()).toBe(false);
    expect(state.session()).toBe(session);
  });
});

describe('the panel editor', () => {
  it('opens the panel on an entry, from the place it was opened from', async () => {
    await state.edit('holmes', { itemId: 'i1', offset: 9 });
    expect(state.open()).toBe(true);
    expect(state.session()).toMatchObject({
      entryId: 'holmes',
      forms: null,
      origin: { itemId: 'i1', offset: 9 },
    });
  });

  it('lights the entry it edits and the row the pointer is on', async () => {
    await state.edit('holmes', null);
    state.hovered.set('watson');
    expect([...state.lit()].sort()).toEqual(['holmes', 'watson']);
    state.hovered.set(null);
    expect([...state.lit()]).toEqual(['holmes']);
  });

  it('asks before an unsaved draft gives way to another entry, and keeps it on No', async () => {
    await state.add({ writtenForm: 'Watson', spokenForm: 'Watson' }, null);
    const draft = state.session();
    dirty = true;
    confirmAnswer = false;

    expect(await state.edit('holmes', null)).toBe(false);
    expect(confirms).toBe(1);
    expect(state.session()).toBe(draft);
  });

  it('does not ask again when the entry already open is opened again', async () => {
    await state.edit('holmes', null);
    dirty = true;
    expect(await state.edit('holmes', { itemId: 'i2', offset: 0 })).toBe(true);
    expect(confirms).toBe(0);
  });

  it('closes when the entry it edits is deleted elsewhere', async () => {
    state.follow('the-adventures-of-sherlock-holmes');
    await sleep(REFETCH_DEBOUNCE_MS + 30);
    expect(state.entries().map((e) => e.id)).toEqual(['holmes']);
    await state.edit('holmes', null);

    list = [];
    live.emit('receipt', {
      folder: 'the-adventures-of-sherlock-holmes',
      effects: { facets: 'Pronunciations' },
    } as unknown as Receipt);
    await sleep(REFETCH_DEBOUNCE_MS + 30);

    expect(state.session()).toBeNull();
  });
});
