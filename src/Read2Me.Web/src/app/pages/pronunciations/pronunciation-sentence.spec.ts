import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { type PronunciationMatchInBook, VIEWING_READ_ONLY } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { IDLE_VOICE_BATCH } from '@app/live/live-state';
import type { QueueMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import type { PronunciationSentence } from './pronunciation-sentence';
import './pronunciation-sentence';

const TRY = '/api/projects/magician/pronunciations/try';

const MATCH: PronunciationMatchInBook = {
  itemId: 'i7',
  chapterId: 'c1',
  chapterTitle: 'ONE - Storm',
  speaker: 'Narrator',
  hasAudio: false,
  sentence: 'So Kulgan said.',
  sentenceStart: 12,
  matchStart: 3,
  matchLength: 6,
  spokenSentence: 'So KULL-gun said.',
  spokenMatchStart: 3,
  spokenMatchLength: 8,
};

let api: FakeApi;
let el: PronunciationSentence;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  api.on('POST', TRY, () => new Promise(() => undefined));
  override(LiveService, {
    queue: signal<QueueMessage | null>(null),
    voiceBatch: signal(IDLE_VOICE_BATCH),
  } as unknown as LiveService);
  override(ToastService, {
    problem: () => undefined,
  } as unknown as ToastService);
});

afterEach(() => el?.remove());

async function mount(readOnly: string | null = null) {
  el = document.createElement('r2m-pronunciation-sentence') as PronunciationSentence;
  el.folder = 'magician';
  el.match = MATCH;
  el.draft = {
    id: null,
    writtenForm: 'Kulgan',
    spokenForm: 'KULL-gun',
    delete: false,
  };
  el.readOnly = readOnly;
  document.body.append(el);
  await settle();
}

async function settle() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
  await new Promise((r) => setTimeout(r, 0));
  await el.rendered();
}

const button = (action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;

describe('the try-out of the sentence the editor was opened from', () => {
  it('shows the sentence as TTS will say it, the spoken form marked', async () => {
    await mount();
    expect(el.querySelector('.pron-sentence__text')!.textContent!.trim()).toBe('So KULL-gun said.');
    expect(el.querySelector('mark')!.textContent).toBe('KULL-gun');
  });

  it('plays that sentence with the draft in the item’s own voice, or as written', async () => {
    await mount();
    button('play-spoken').click();
    await settle();
    button('play-written').click();
    await settle();
    expect(api.calls('POST', TRY).map((c) => c.body)).toEqual([
      {
        itemId: 'i7',
        sentenceStart: 12,
        draft: {
          id: null,
          writtenForm: 'Kulgan',
          spokenForm: 'KULL-gun',
          delete: false,
        },
        asWritten: false,
        voice: 'item',
      },
      {
        itemId: 'i7',
        sentenceStart: 12,
        draft: {
          id: null,
          writtenForm: 'Kulgan',
          spokenForm: 'KULL-gun',
          delete: false,
        },
        asWritten: true,
        voice: 'item',
      },
    ]);
  });

  it('is off with the viewing reason while a step is viewed', async () => {
    await mount(VIEWING_READ_ONLY);
    expect(button('play-spoken').disabled).toBe(true);
    expect(button('play-spoken').closest('.r2m-blockable')!.getAttribute('data-tooltip')).toBe(
      VIEWING_READ_ONLY,
    );
  });
});
