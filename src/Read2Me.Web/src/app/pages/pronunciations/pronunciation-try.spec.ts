import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  type CharacterSummaryDto,
  type PronunciationMatchInBook,
  VIEWING_READ_ONLY,
  type VoiceDto,
} from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_VOICE_BATCH } from '@app/live/live-state';
import type { QueueMessage, VoiceBatchState } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import type { PronunciationTry } from './pronunciation-try';
import './pronunciation-try';

const BASE = '/api/projects/magician';
const TRY = `${BASE}/pronunciations/try`;

const character = (id: string, name: string, flags: Partial<CharacterSummaryDto> = {}) =>
  ({
    id,
    name,
    aliases: [],
    lineCount: 1,
    voiceCount: 1,
    readyVoiceCount: 1,
    isNarrator: false,
    narratesBook: false,
    ...flags,
  }) as CharacterSummaryDto;
const voice = (id: string, name: string) => ({ id, name, audioFileName: `${id}.wav` }) as VoiceDto;

/** Match `i`, item `i{i}`, in chapter "Ch {i}". */
function match(i: number, speaker: string | null = 'Narrator'): PronunciationMatchInBook {
  return {
    itemId: `i${i}`,
    chapterId: `c${i}`,
    chapterTitle: `Ch ${i}`,
    speaker,
    hasAudio: i % 2 === 0,
    sentence: `So Kulgan said ${i}.`,
    sentenceStart: 4,
    matchStart: 3,
    matchLength: 6,
    spokenSentence: `So KULL-gun said ${i}.`,
    spokenMatchStart: 3,
    spokenMatchLength: 8,
  };
}

let api: FakeApi;
let queue: ReturnType<typeof signal<QueueMessage | null>>;
let voiceBatch: ReturnType<typeof signal<VoiceBatchState>>;
let el: PronunciationTry;
let problems: (string | undefined)[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  queue = signal<QueueMessage | null>(null);
  voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  override(LiveService, { queue, voiceBatch } as unknown as LiveService);
  problems = [];
  override(ToastService, {
    problem: (p: { detail?: string }) => problems.push(p.detail),
  } as unknown as ToastService);
  api.on('GET', `${BASE}/characters/summary`, [
    character('pug', 'Pug'),
    character('nar', 'Narrator', { isNarrator: true }),
    character('tomas', 'Tomas', { readyVoiceCount: 0 }),
  ]);
  api.on('GET', `${BASE}/characters/nar/voices`, {
    defaultVoiceId: 'n1',
    voices: [voice('n1', 'Warm')],
  });
  api.on('GET', `${BASE}/characters/pug/voices`, {
    defaultVoiceId: 'p1',
    voices: [voice('p1', 'Young')],
  });
});

afterEach(() => el?.remove());

async function mount(matches: PronunciationMatchInBook[], readOnly: string | null = null) {
  el = document.createElement('r2m-pronunciation-try') as PronunciationTry;
  el.folder = 'magician';
  el.matchesInBook = matches;
  el.draft = { id: 'k1', writtenForm: 'Kulgan', spokenForm: 'KOOL-gan', delete: false };
  el.readOnly = readOnly;
  document.body.append(el);
  await settle();
  return el;
}

async function settle() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
  await new Promise((r) => setTimeout(r, 0));
  await el.rendered();
}

const cards = () => [...el.querySelectorAll<HTMLElement>('.pron-try__card')];
const play = (key: string) => el.querySelector<HTMLButtonElement>(`[data-play="${key}"]`)!;
const reasonOf = (button: HTMLElement) =>
  button.closest('.r2m-blockable')!.getAttribute('data-tooltip');

describe('Try it', () => {
  it('shows 3 cards with chapter · speaker · audio and the spoken form marked', async () => {
    await mount([match(0), match(1, null), match(2), match(3)]);
    expect(cards()).toHaveLength(3);
    const first = cards()[0]!;
    expect(first.querySelector('[data-role=meta]')!.textContent!.replace(/\s+/g, ' ').trim()).toBe(
      'Ch 0 · Narrator · has audio',
    );
    expect(first.querySelector('.pron-try__mark')!.textContent).toBe('KULL-gun');
    expect(
      first.querySelector('.pron-try__sentence')!.textContent!.replace(/\s+/g, ' ').trim(),
    ).toBe('So KULL-gun said 0.');
    expect(cards()[1]!.querySelector('[data-role=meta]')!.textContent).toContain('Unattributed');
  });

  it('lists voices by character, the narrator first, then the item voice, defaulting to the narrator', async () => {
    await mount([match(0)]);
    const select = cards()[0]!.querySelector<HTMLSelectElement>('[data-role=voice]')!;
    expect([...select.querySelectorAll('optgroup')].map((g) => g.label)).toEqual([
      'Narrator',
      'Pug',
    ]);
    expect([...select.options].map((o) => o.textContent!.trim())).toEqual([
      'Narrator: Warm',
      'Pug: Young',
      "This item's own voice",
    ]);
    expect(select.value).toBe('n1');
  });

  it('hides the picker when the project has no voices, and cards use the item voice', async () => {
    api.on('GET', `${BASE}/characters/summary`, [
      character('nar', 'Narrator', { isNarrator: true, readyVoiceCount: 0 }),
    ]);
    api.on('POST', TRY, () => new Promise(() => undefined));
    await mount([match(0)]);
    expect(el.querySelector('[data-role=voice]')).toBeNull();
    play(`${'i0:4:3'}|spoken`).click();
    await settle();
    expect(api.calls('POST', TRY)[0]!.body).toMatchObject({ voice: 'item' });
  });

  it('plays the spoken form with the draft, or as written, in the card voice; Generating… meanwhile', async () => {
    api.on('POST', TRY, () => new Promise(() => undefined));
    await mount([match(0)]);
    const select = cards()[0]!.querySelector<HTMLSelectElement>('[data-role=voice]')!;
    select.value = 'p1';
    select.dispatchEvent(new Event('change'));
    await settle();

    play('i0:4:3|spoken').click();
    await settle();
    expect(play('i0:4:3|spoken').textContent).toContain('Generating…');
    play('i0:4:3|written').click();
    await settle();
    expect(play('i0:4:3|spoken').textContent).not.toContain('Generating…');

    expect(api.calls('POST', TRY).map((c) => c.body)).toEqual([
      {
        itemId: 'i0',
        sentenceStart: 4,
        draft: { id: 'k1', writtenForm: 'Kulgan', spokenForm: 'KOOL-gan', delete: false },
        asWritten: false,
        voice: { voiceId: 'p1' },
      },
      {
        itemId: 'i0',
        sentenceStart: 4,
        draft: { id: 'k1', writtenForm: 'Kulgan', spokenForm: 'KOOL-gan', delete: false },
        asWritten: true,
        voice: { voiceId: 'p1' },
      },
    ]);
  });

  it('Swap shows the next unshown match and keeps the card voice', async () => {
    await mount([match(0), match(1), match(2), match(3), match(4)]);
    const before = cards().map((c) => c.dataset['key']);
    const select = cards()[0]!.querySelector<HTMLSelectElement>('[data-role=voice]')!;
    select.value = 'p1';
    select.dispatchEvent(new Event('change'));
    await settle();

    cards()[0]!.querySelector<HTMLButtonElement>('[data-action=swap]')!.click();
    await settle();
    const after = cards()[0]!;
    expect(after.dataset['key']).not.toBe(before[0]);
    expect(before).not.toContain(after.dataset['key']);
    expect(after.querySelector<HTMLSelectElement>('[data-role=voice]')!.value).toBe('p1');
  });

  it('All N matches lists every match, a tick making it a card and dropping the oldest', async () => {
    await mount([match(0), match(1), match(2), match(3), match(4)]);
    el.querySelector<HTMLButtonElement>('[data-action=all-matches]')!.click();
    await settle();
    const rows = [...el.querySelectorAll<HTMLElement>('.pron-try__row')];
    expect(rows).toHaveLength(5);
    const unticked = rows.find((r) => !r.querySelector<HTMLInputElement>('input')!.checked)!;
    const oldest = cards()[0]!.dataset['key'];
    unticked.querySelector<HTMLInputElement>('input')!.click();
    await settle();
    expect(cards().map((c) => c.dataset['key'])).toContain(unticked.dataset['key']);
    expect(cards().map((c) => c.dataset['key'])).not.toContain(oldest);
    expect(cards()).toHaveLength(3);
  });

  it('turns every play off with its reason while a GPU job runs, and while viewing a step', async () => {
    await mount([match(0)]);
    queue.set({
      attribution: { queuedCount: 2, processingCount: 0, isBusy: true },
      audio: { queuedCount: 0, processingCount: 0 },
    } as QueueMessage);
    await settle();
    for (const key of ['all', 'i0:4:3|spoken', 'i0:4:3|written']) {
      expect(play(key).disabled).toBe(true);
      expect(reasonOf(play(key))).toBe("Can't play while Attribution is running: it has the GPU");
    }

    el.remove();
    await mount([match(0)], VIEWING_READ_ONLY);
    expect(play('i0:4:3|written').disabled).toBe(true);
    expect(reasonOf(play('i0:4:3|written'))).toBe(VIEWING_READ_ONLY);
  });

  it('toasts why the host refused a play', async () => {
    api.on(
      'POST',
      TRY,
      () =>
        new Response(
          JSON.stringify({ status: 409, detail: 'Can’t play while a voice batch is running.' }),
          {
            status: 409,
          },
        ),
    );
    await mount([match(0)]);
    play('i0:4:3|written').click();
    await settle();
    expect(problems).toEqual(['Can’t play while a voice batch is running.']);
  });
});
