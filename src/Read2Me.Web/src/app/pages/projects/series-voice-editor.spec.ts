import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { SeriesCharacterDto, VoiceDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService, type ConfirmOptions, PromptService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { settle } from '../../../testing/fake-navigation';
import { ProviderSchemas } from '../cast/voices/provider-schemas';
import type { AddVoiceDialog } from '../cast/voices/add-voice-dialog';
import type { SeriesVoiceEditor } from './series-voice-editor';
import './series-voice-editor';

const DETAIL = '/api/series/h/characters/sc-watson';
const SCARLET = '/api/projects/scarlet';
const COMMANDS = `${SCARLET}/commands`;

function voice(id: string, name: string, overrides: Partial<VoiceDto> = {}): VoiceDto {
  return {
    id,
    characterId: 's-watson',
    name,
    description: null,
    source: 'Uploaded',
    designPrompt: null,
    transcript: null,
    audioFileName: null,
    isEdited: false,
    voiceDesignSettingsOverrideJson: null,
    ttsSettingsOverrideJson: null,
    referenceSeconds: null,
    referenceWarning: null,
    ...overrides,
  };
}

const book = (
  folder: string,
  title: string,
  position: number,
  characterId: string,
  defaultVoiceId: string | null,
) => ({
  folder,
  title,
  position,
  characterId,
  characterName: 'Watson',
  defaultVoiceId,
  differs: false,
});

function detail(overrides: Partial<SeriesCharacterDto> = {}): SeriesCharacterDto {
  return {
    seriesCopyFolder: 'scarlet',
    voices: [
      voice('v1', 'Army doctor', { audioFileName: 'voices/s-watson/v1.wav', description: 'warm' }),
      voice('v2', 'Old campaigner'),
    ],
    books: [
      book('scarlet', 'A Study in Scarlet', 1, 's-watson', 'v1'),
      book('four', 'The Sign of the Four', 2, 'f-watson', 'v2'),
      book('adventures', 'The Adventures of Sherlock Holmes', 3, 'a-watson', 'v1'),
    ],
    deleteBlocks: {},
    ...overrides,
  };
}

let api: FakeApi;
let live: FakeLive;
let toasts: string[];
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let promptAnswer: string | null;
let prompts: { title: string; hint?: string }[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive();
  override(LiveService, live as unknown as LiveService);
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(`success: ${m}`),
    warn: (m: string) => toasts.push(`warn: ${m}`),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  prompts = [];
  promptAnswer = null;
  override(PromptService, {
    text: async (o: { title: string; hint?: string }) => {
      prompts.push(o);
      return promptAnswer;
    },
  } as unknown as PromptService);
  override(Preflight, { ensureReady: async () => true } as unknown as Preflight);
  override(ProviderSchemas, { active: async () => null } as unknown as ProviderSchemas);
});
afterEach(() => document.body.replaceChildren());

/** The editor over `answer`, with the series copy's book (Scarlet) behind its voice cards. */
async function mount(answer: SeriesCharacterDto | (() => SeriesCharacterDto) = detail()) {
  api.on('GET', DETAIL, typeof answer === 'function' ? answer : () => answer);
  api.on('GET', `${SCARLET}/characters/summary`, []);
  api.on('GET', `${SCARLET}/characters/s-watson/lines`, []);
  api.on('GET', `${SCARLET}/characters/s-watson/voices`, () => {
    const d = typeof answer === 'function' ? answer() : answer;
    return { defaultVoiceId: 'v1', voices: d.voices };
  });
  api.on('GET', `${SCARLET}/characters/s-watson/voice-rules`, []);
  api.on('GET', `${SCARLET}/characters/s-watson/voice-rules/preview`, []);
  api.on('POST', COMMANDS, { outcome: 'Committed', newEntityId: 'v3' });
  const el = document.createElement('r2m-series-voice-editor') as SeriesVoiceEditor;
  el.seriesId = 'h';
  el.seriesCharacterId = 'sc-watson';
  el.label = 'Dr. Watson';
  document.body.append(el);
  await settle();
  await settle();
  await el.rendered();
  return el;
}

const rowOf = (el: Element, id: string) =>
  el.querySelector<HTMLElement>(`tr[data-voice-id="${id}"]`)!;
const action = (el: Element, name: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${name}"]`)!;
const commands = (path = COMMANDS) => api.calls('POST', path).map((r) => r.body);
const text = (el: Element | null | undefined) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('r2m-series-voice-editor', () => {
  it('shows the series copy’s Voices with a Default in Book N column per linked book', async () => {
    const el = await mount();

    expect(Array.from(el.querySelectorAll('thead th'), text)).toEqual([
      'Voice',
      'Default in Book 1 as Watson',
      'Default in Book 2 as Watson',
      'Default in Book 3 as Watson',
      '',
    ]);
    expect(text(rowOf(el, 'v1').querySelector('.series-voices__voice'))).toBe('Army doctor warm');
    const checked = (id: string) =>
      Array.from(
        rowOf(el, id).querySelectorAll<HTMLInputElement>('input[type=radio]'),
        (r) => r.checked,
      );
    expect(checked('v1')).toEqual([true, false, true]);
    expect(checked('v2')).toEqual([false, true, false]);
    // Play only for a Voice with audio.
    expect(rowOf(el, 'v1').querySelector('r2m-audio-player')?.src).toBe(
      '/workspace/scarlet/voices/s-watson/v1.wav?v=0',
    );
    expect(rowOf(el, 'v2').querySelector('r2m-audio-player')).toBeNull();
  });

  it('says so while reading, and when there are no Voices yet', async () => {
    api.on('GET', DETAIL, () => new Promise(() => undefined));
    const reading = document.createElement('r2m-series-voice-editor') as SeriesVoiceEditor;
    reading.seriesId = 'h';
    reading.seriesCharacterId = 'sc-watson';
    document.body.append(reading);
    await reading.rendered();
    expect(text(reading)).toContain('Reading the Voices');

    document.body.replaceChildren();
    const empty = await mount(detail({ voices: [] }));
    expect(text(empty)).toContain('No Voices yet');
    expect(empty.querySelector('table')).toBeNull();
  });

  it('a refused read says why', async () => {
    api.on('GET', DETAIL, () =>
      Response.json(
        { title: 'Not Found', status: 404, detail: 'No such series character.' },
        { status: 404 },
      ),
    );
    const el = document.createElement('r2m-series-voice-editor') as SeriesVoiceEditor;
    el.seriesId = 'h';
    el.seriesCharacterId = 'sc-watson';
    document.body.append(el);
    await settle();
    await el.rendered();
    expect(text(el)).toContain('The series Voices could not be read');
  });

  it('rename prompts with where it is saved, writes to the series copy and toasts "Saved in N books"', async () => {
    const el = await mount();
    promptAnswer = 'Old soldier';

    action(rowOf(el, 'v1'), 'rename').click();
    await settle();
    await settle();

    expect(prompts[0]?.hint).toBe(
      'Saved in A Study in Scarlet, The Sign of the Four and The Adventures of Sherlock Holmes.',
    );
    expect(commands()).toEqual([
      { type: 'UpdateVoice', voiceId: 'v1', name: 'Old soldier', description: 'warm' },
    ]);
    expect(toasts).toEqual(['success: Saved in 3 books']);
    expect(api.calls('GET', DETAIL).length).toBe(2); // read again after the write
  });

  it('delete is off with its note while another book’s rules use the Voice, or it is the last', async () => {
    const el = await mount(
      detail({
        deleteBlocks: {
          v1: {
            reason: 'PositionalRules',
            books: [{ folder: 'four', title: 'The Sign of the Four', ruleCount: 2 }],
          },
        },
      }),
    );

    expect(action(rowOf(el, 'v1'), 'delete').disabled).toBe(true);
    expect(text(rowOf(el, 'v1').querySelector('[data-testid=delete-note]'))).toContain(
      "Can't delete: Voice Rules in The Sign of the Four use it.",
    );
    expect(action(rowOf(el, 'v2'), 'delete').disabled).toBe(false);
    expect(rowOf(el, 'v2').querySelector('[data-testid=delete-note]')).toBeNull();

    document.body.replaceChildren();
    const last = await mount(
      detail({
        voices: [voice('v1', 'Army doctor')],
        deleteBlocks: {
          v1: {
            reason: 'LastVoice',
            books: [
              { folder: 'four', title: 'The Sign of the Four', ruleCount: 0 },
              { folder: 'adventures', title: 'The Adventures of Sherlock Holmes', ruleCount: 0 },
            ],
          },
        },
      }),
    );
    expect(action(rowOf(last, 'v1'), 'delete').disabled).toBe(true);
    expect(text(rowOf(last, 'v1').querySelector('[data-testid=delete-note]'))).toContain(
      "Can't delete the last Voice",
    );
  });

  it('delete asks in red, naming every linked book, then deletes through the series copy', async () => {
    const el = await mount();

    confirmAnswer = false;
    action(rowOf(el, 'v2'), 'delete').click();
    await settle();
    expect(confirms[0]?.title).toBe('Delete "Old campaigner" from 3 books?');
    expect(confirms[0]?.destructive).toBe(true);
    expect(confirms[0]?.message).toContain(
      'A Study in Scarlet, The Sign of the Four and The Adventures of Sherlock Holmes',
    );
    expect(commands()).toEqual([]);

    confirmAnswer = true;
    action(rowOf(el, 'v2'), 'delete').click();
    await settle();
    await settle();
    expect(commands()).toEqual([{ type: 'DeleteVoice', voiceId: 'v2' }]);
  });

  it('a refused delete toasts the host’s reason', async () => {
    const el = await mount();
    api.on('POST', COMMANDS, () =>
      Response.json(
        { title: 'Conflict', status: 409, detail: 'Voice Rules use it in Four.' },
        { status: 409 },
      ),
    );
    action(rowOf(el, 'v2'), 'delete').click();
    await settle();
    await settle();
    expect(toasts).toEqual(['problem: Voice Rules use it in Four.']);
  });

  it('Default in Book N sets that book’s own default, with no "Saved in" toast', async () => {
    const el = await mount();
    api.on('POST', '/api/projects/four/commands', { outcome: 'Committed' });

    const radio = rowOf(el, 'v1').querySelector<HTMLInputElement>('td[data-folder=four] input')!;
    radio.checked = true;
    radio.dispatchEvent(new Event('change'));
    await settle();
    await settle();

    expect(commands('/api/projects/four/commands')).toEqual([
      { type: 'SetVoiceDefault', voiceId: 'v1' },
    ]);
    expect(commands()).toEqual([]);
    expect(toasts).toEqual([]);
  });

  it('Add Voice creates it for the series copy’s Character and opens its full editor', async () => {
    let current = detail();
    const el = await mount(() => current);
    api.on('POST', COMMANDS, () => {
      current = detail({ voices: [...current.voices, voice('v3', 'Retired')] });
      return { outcome: 'Committed', newEntityId: 'v3' };
    });

    action(el, 'add-voice').click();
    await settle();
    const dialog = document.querySelector<AddVoiceDialog>('r2m-add-voice-dialog')!;
    await dialog.rendered();
    dialog.querySelector<HTMLButtonElement>('[data-action=add]')!.click();
    await settle();
    await settle();
    await el.rendered();

    expect(commands()).toEqual([
      { type: 'CreateVoice', characterId: 's-watson', name: 'Dr. Watson', isGenerated: false },
    ]);
    expect(toasts).toEqual(['success: Saved in 3 books']);
    expect(el.expanded()).toBe('v3');
    expect(el.querySelector('tr[data-editor-for=v3] r2m-voice-card')).not.toBeNull();
  });

  it('Edit opens the full voice card under its row, over the series copy’s book', async () => {
    const el = await mount();

    action(rowOf(el, 'v1'), 'edit').click();
    await el.rendered();

    const editorRow = rowOf(el, 'v1').nextElementSibling!;
    expect(editorRow.getAttribute('data-editor-for')).toBe('v1');
    const card = editorRow.querySelector('r2m-voice-card')!;
    await card.rendered();
    expect(card.voice?.id).toBe('v1');
    // Not read-only here: this is where series Voices are edited.
    expect(card.querySelector<HTMLButtonElement>('[data-action=delete-voice]')!.disabled).toBe(
      false,
    );
    expect(action(rowOf(el, 'v1'), 'edit').getAttribute('aria-expanded')).toBe('true');

    action(rowOf(el, 'v1'), 'edit').click();
    await el.rendered();
    expect(el.querySelector('tr[data-editor-for]')).toBeNull();
  });

  it('reads again on the series signal for its series only', async () => {
    const el = await mount();
    const reads = () => api.calls('GET', DETAIL).length;
    expect(reads()).toBe(1);

    live.emit('series', { seriesId: 'other', kind: 'Changed' });
    await settle();
    expect(reads()).toBe(1);

    live.emit('series', { seriesId: 'h', kind: 'Changed' });
    await settle();
    expect(reads()).toBe(2);
    await el.rendered();
  });
});
