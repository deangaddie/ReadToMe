import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { PronunciationDto } from '@app/api';
import { ActivityStore } from '@app/activity/activity-store';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_VOICE_BATCH } from '@app/live/live-state';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import type { PronunciationEditor } from '../pronunciations/pronunciation-editor';
import { indexSpans } from './pronunciation-marks';
import type { PronunciationPanel } from './pronunciation-panel';
import { PronunciationPanelState } from './pronunciation-panel-store';
import './pronunciation-panel';

const BASE = '/api/projects/magician/pronunciations';

const entry = (id: string, writtenForm: string, spokenForm: string): PronunciationDto => ({
  id,
  writtenForm,
  spokenForm,
  matchCount: 1,
  itemCount: 1,
  withAudioCount: 0,
});
const PUG = entry('pug', 'Pug', 'PUHG');
const KULGAN = entry('kulgan', 'Kulgan', 'KULL-gun');
const TOMAS = entry('tomas', 'Tomas', 'TOM-us');

const span = (itemId: string, start: number, pronunciationId: string) => ({
  itemId,
  start,
  length: 3,
  pronunciationId,
  sentenceStart: 0,
});

const marks = indexSpans({
  pronunciations: [PUG, KULGAN],
  spans: [span('i1', 0, 'pug'), span('i1', 8, 'kulgan'), span('i2', 4, 'pug')],
});

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

let drawerOpen: ReturnType<typeof signal<boolean>>;
let state: PronunciationPanelState;
let panel: PronunciationPanel;

beforeEach(async () => {
  resetServices();
  const api = new FakeApi();
  api.install();
  api.on('GET', BASE, [KULGAN, PUG, TOMAS]);
  api.on('POST', `${BASE}/impact`, {
    matchCount: 0,
    itemCount: 0,
    withAudioCount: 0,
    audioDiscardCount: 0,
    overlaps: [],
    duplicateOf: null,
    matches: [],
  });
  const live = new FakeLive().install();
  live.voiceBatch.set(IDLE_VOICE_BATCH);
  drawerOpen = signal(false);
  override(ActivityStore, {
    drawerOpen,
    closeDrawer: () => drawerOpen.set(false),
  } as unknown as ActivityStore);
  override(ConfirmService, {
    confirm: async () => true,
  } as unknown as ConfirmService);
  override(ToastService, {} as unknown as ToastService);
  override(Router, { navigate: () => undefined } as unknown as Router);

  panel = document.createElement('r2m-pronunciation-panel') as PronunciationPanel;
  state = new PronunciationPanelState(() => panel.dirty());
  state.follow('magician');
  panel.state = state;
  panel.folder = 'magician';
  panel.marks = marks;
  panel.itemsWithAudio = new Set(['i2']);
  panel.readOnly = null;
  panel.audioInFlight = 0;
  document.body.append(panel);
  state.show();
  await sleep(REFETCH_DEBOUNCE_MS + 30);
  await panel.rendered();
});

afterEach(() => {
  panel.remove();
  state.dispose();
});

const rows = () => [...panel.querySelectorAll<HTMLElement>('[data-role=row]')];
const editor = () => panel.querySelector<PronunciationEditor>('r2m-pronunciation-editor');
const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the Pronunciations panel', () => {
  it('lists the entries in the loaded chapters with their count and items with audio, and links to the whole list', () => {
    expect(
      rows().map((r) =>
        ['written', 'spoken', 'count'].map((p) => text(r.querySelector(`.pron-panel__${p}`)!)),
      ),
    ).toEqual([
      ['Pug', 'PUHG', '2 · 1 audio'],
      ['Kulgan', 'KULL-gun', '1 · 0 audio'],
    ]);
    const all = panel.querySelector<HTMLAnchorElement>('[data-role=all]')!;
    expect(text(all)).toStartWith('All 3 on the Pronunciations page');
    expect(all.getAttribute('href')).toBe('projects/magician/pronunciations');
  });

  it('lights the matches of the row the pointer is on', async () => {
    rows()[1]!.dispatchEvent(new PointerEvent('pointerenter'));
    expect([...state.lit()]).toEqual(['kulgan']);
    rows()[1]!.dispatchEvent(new PointerEvent('pointerleave'));
    expect([...state.lit()]).toEqual([]);
  });

  it('opens a row in the shared editor, from its first match in the loaded chapters', async () => {
    rows()[1]!.querySelector('button')!.click();
    await sleep(0);
    await panel.rendered();
    expect(editor()!.entry?.id).toBe('kulgan');
    expect(editor()!.origin).toEqual({ itemId: 'i1', offset: 8 });
    expect([...state.lit()]).toEqual(['kulgan']);
  });

  it('keeps an unsaved draft while the Activity drawer has the space, and shows it again', async () => {
    await state.add({ writtenForm: 'Crydee', spokenForm: 'Crydee' }, null);
    await panel.rendered();
    const input = editor()!.querySelector<HTMLInputElement>('[data-field=spoken]')!;
    input.value = 'CRY-dee';
    input.dispatchEvent(new Event('input', { bubbles: true }));

    drawerOpen.set(true);
    await panel.rendered();
    expect(panel.hidden).toBe(true);

    state.show();
    await panel.rendered();
    expect(panel.hidden).toBe(false);
    expect(drawerOpen()).toBe(false);
    expect(editor()!.spoken()).toBe('CRY-dee');
    expect(panel.dirty()).toBe(true);
  });

  it('closes its editor when the editor is done', async () => {
    await state.edit('pug', null);
    await panel.rendered();
    editor()!.querySelector<HTMLButtonElement>('[data-action=cancel]')!.click();
    await panel.rendered();
    expect(editor()).toBeNull();
    expect(state.session()).toBeNull();
  });
});
