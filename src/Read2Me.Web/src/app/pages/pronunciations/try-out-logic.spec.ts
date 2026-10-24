import { describe, expect, it } from 'bun:test';
import type {
  CharacterSummaryDto,
  CharacterVoicesDto,
  PronunciationMatchInBook,
  VoiceDto,
} from '@app/api';
import { IDLE_VOICE_BATCH } from '@app/live/live-state';
import type { QueueMessage } from '@app/live/live-messages';
import {
  originMatch,
  playView,
  cardsFor,
  gpuBusyReason,
  matchKey,
  otherSampleBlockedReason,
  pickSamples,
  playBlockedReason,
  reshuffle,
  type SampleCard,
  spokenPlayBlockedReason,
  swap,
  toggleCard,
  voicePicker,
} from './try-out-logic';

/** Match `i` of the book, spoken by `speaker`; its key is `i${i}:0:0`. */
function match(i: number, speaker: string | null = 'Narrator'): PronunciationMatchInBook {
  return {
    itemId: `i${i}`,
    chapterId: 'c1',
    chapterTitle: 'Chapter 1',
    speaker,
    hasAudio: false,
    sentence: `Kulgan ${i}.`,
    sentenceStart: 0,
    matchStart: 0,
    matchLength: 6,
    spokenSentence: `KULL-gun ${i}.`,
    spokenMatchStart: 0,
    spokenMatchLength: 8,
  };
}

const keyOf = (i: number) => matchKey(match(i));

describe('pickSamples', () => {
  it('shows every match when there are three or fewer', () => {
    expect(pickSamples([match(0), match(1)])).toEqual([keyOf(0), keyOf(1)]);
  });

  it('picks the first, middle and last match when one speaker says them all', () => {
    const matches = Array.from({ length: 9 }, (_, i) => match(i));
    expect(pickSamples(matches)).toEqual([keyOf(0), keyOf(4), keyOf(8)]);
  });

  it('prefers a different speaker nearest each anchor, within its third', () => {
    const speakers = ['A', 'A', 'B', 'A', 'A', 'B', 'A', 'C', 'A'];
    const matches = speakers.map((s, i) => match(i, s));
    // First third 0–2: A at 0. Middle 3–5 anchor 4: A taken, B at 5. Last 6–8 anchor 8: C at 7.
    expect(pickSamples(matches)).toEqual([keyOf(0), keyOf(5), keyOf(7)]);
  });
});

describe('cards', () => {
  const matches = Array.from({ length: 6 }, (_, i) => match(i));
  const narrator = { voiceId: 'v-narrator' };

  it('keeps the cards while their matches stand, and picks afresh keeping voices when not', () => {
    const cards = [
      { key: keyOf(1), voice: 'item' as const },
      { key: keyOf(2), voice: narrator },
    ];
    expect(cardsFor(matches, cards, narrator)).toEqual(cards);
    const fresh = cardsFor([match(10), match(11), match(12), match(13)], cards, narrator);
    expect(fresh.map((c) => c.voice)).toEqual(['item', narrator, narrator]);
    expect(fresh[0]!.key).toBe(keyOf(10));
  });

  it('Swap moves a card to the next match no card shows, wrapping, keeping its voice', () => {
    const cards = [
      { key: keyOf(0), voice: narrator },
      { key: keyOf(1), voice: narrator },
      { key: keyOf(5), voice: 'item' as const },
    ];
    expect(swap(matches, cards, 0)[0]).toEqual({
      key: keyOf(2),
      voice: narrator,
    });
    expect(swap(matches, cards, 2)[2]).toEqual({
      key: keyOf(2),
      voice: 'item',
    });
    expect(swap(matches.slice(0, 2), cards.slice(0, 2), 0)).toEqual(cards.slice(0, 2));
  });

  it('Other samples draws matches not shown, each card keeping its voice', () => {
    const cards = [
      { key: keyOf(0), voice: narrator },
      { key: keyOf(1), voice: 'item' as const },
      { key: keyOf(2), voice: narrator },
    ];
    const next = reshuffle(matches, cards, () => 0);
    expect(next).toHaveLength(3);
    expect(next.map((c) => c.voice)).toEqual([narrator, 'item', narrator]);
    expect(next.some((c) => [keyOf(0), keyOf(1), keyOf(2)].includes(c.key))).toBe(false);
  });

  it('a tick adds the newest card, the oldest dropping past three; ticking again removes it', () => {
    let cards: SampleCard[] = [keyOf(0), keyOf(1), keyOf(2)].map((key) => ({
      key,
      voice: narrator,
    }));
    cards = toggleCard(cards, keyOf(4), 'item');
    expect(cards.map((c) => c.key)).toEqual([keyOf(1), keyOf(2), keyOf(4)]);
    expect(toggleCard(cards, keyOf(2), 'item').map((c) => c.key)).toEqual([keyOf(1), keyOf(4)]);
  });
});

describe('voicePicker', () => {
  const character = (id: string, name: string, flags: Partial<CharacterSummaryDto> = {}) =>
    ({
      id,
      name,
      aliases: [],
      lineCount: 0,
      voiceCount: 1,
      readyVoiceCount: 1,
      isNarrator: false,
      narratesBook: false,
      ...flags,
    }) as CharacterSummaryDto;
  const voice = (id: string, name: string, audio = true) =>
    ({ id, name, audioFileName: audio ? `${id}.wav` : null }) as VoiceDto;
  const voicesOf = (defaultVoiceId: string | null, ...voices: VoiceDto[]): CharacterVoicesDto => ({
    defaultVoiceId,
    voices,
  });

  it('lists voices by character, the narrator first, defaulting to its default voice', () => {
    const cast = [
      character('pug', 'Pug'),
      character('kulgan', 'Kulgan'),
      character('nar', 'Narrator', { isNarrator: true }),
    ];
    const voices = new Map([
      ['pug', voicesOf('p1', voice('p1', 'Pug young'))],
      ['kulgan', voicesOf('k1', voice('k1', 'Kulgan'), voice('k2', 'Kulgan draft', false))],
      ['nar', voicesOf('n2', voice('n1', 'Warm'), voice('n2', 'Dry'))],
    ]);
    const picker = voicePicker(cast, voices);
    expect(picker.groups).toEqual([
      {
        character: 'Narrator',
        voices: [
          { id: 'n1', name: 'Warm' },
          { id: 'n2', name: 'Dry' },
        ],
      },
      { character: 'Kulgan', voices: [{ id: 'k1', name: 'Kulgan' }] },
      { character: 'Pug', voices: [{ id: 'p1', name: 'Pug young' }] },
    ]);
    expect(picker.defaultVoice).toEqual({ voiceId: 'n2' });
  });

  it('puts the character the narrator link names first', () => {
    const cast = [
      character('nar', 'Narrator', { isNarrator: true }),
      character('pug', 'Pug', { narratesBook: true }),
    ];
    const voices = new Map([
      ['nar', voicesOf('n1', voice('n1', 'Warm'))],
      ['pug', voicesOf('p1', voice('p1', 'Pug'))],
    ]);
    const picker = voicePicker(cast, voices);
    expect(picker.groups.map((g) => g.character)).toEqual(['Pug', 'Narrator']);
    expect(picker.defaultVoice).toEqual({ voiceId: 'p1' });
  });

  it('falls back to the first voice without a narrator voice, and the item voice without any', () => {
    const cast = [character('nar', 'Narrator', { isNarrator: true }), character('pug', 'Pug')];
    expect(
      voicePicker(cast, new Map([['pug', voicesOf(null, voice('p1', 'Pug'))]])).defaultVoice,
    ).toEqual({ voiceId: 'p1' });
    expect(voicePicker(cast, new Map())).toEqual({
      groups: [],
      defaultVoice: 'item',
    });
  });
});

describe('why playing is off', () => {
  const queue = (attribution: number, audio: number): QueueMessage =>
    ({
      attribution: {
        queuedCount: attribution,
        processingCount: 0,
        isBusy: attribution > 0,
      },
      audio: { queuedCount: audio, processingCount: 0 },
    }) as QueueMessage;

  it('names whatever has the GPU, attribution first', () => {
    expect(gpuBusyReason(queue(2, 3), IDLE_VOICE_BATCH)).toBe(
      "Can't play while Attribution is running: it has the GPU",
    );
    expect(gpuBusyReason(queue(0, 3), IDLE_VOICE_BATCH)).toBe(
      "Can't play while Audio generation is running: it has the GPU",
    );
    expect(gpuBusyReason(queue(0, 0), { ...IDLE_VOICE_BATCH, isRunning: true })).toBe(
      "Can't play while a voice batch is running: it has the GPU",
    );
    expect(gpuBusyReason(queue(0, 0), IDLE_VOICE_BATCH)).toBeNull();
    expect(gpuBusyReason(null, IDLE_VOICE_BATCH)).toBeNull();
  });

  it('says viewing a step before the GPU', () => {
    expect(playBlockedReason('Viewing a step', 'GPU')).toBe('Viewing a step');
    expect(playBlockedReason(null, 'GPU')).toBe('GPU');
    expect(playBlockedReason(null, null)).toBeNull();
  });
});

describe('why the other controls are off', () => {
  it('spoken-form plays wait for a spoken form', () => {
    expect(spokenPlayBlockedReason('  ')).toBe('Type a spoken form to hear it');
    expect(spokenPlayBlockedReason(null)).toBe('Type a spoken form to hear it');
    expect(spokenPlayBlockedReason('KULL-gun')).toBeNull();
  });

  it('Swap and Other samples need a match no card shows', () => {
    expect(otherSampleBlockedReason(3, 3)).toBe('Every match is already a sample');
    expect(otherSampleBlockedReason(4, 3)).toBeNull();
  });
});

describe('playView', () => {
  const reason = "Can't play while Attribution is running: it has the GPU";

  it('plays when nothing blocks it, and is off with the reason while blocked', () => {
    expect(playView('k', null, null)).toEqual({ phase: 'idle', disabled: false, reason: null });
    expect(playView('k', reason, null)).toEqual({ phase: 'idle', disabled: true, reason });
  });

  it('follows its own sentence through generating and playing, stoppable though blocked since', () => {
    expect(playView('k', null, { key: 'k', phase: 'generating' }).phase).toBe('generating');
    expect(playView('k', reason, { key: 'k', phase: 'playing' })).toEqual({
      phase: 'playing',
      disabled: false,
      reason: null,
    });
    expect(playView('k', null, { key: 'other', phase: 'playing' }).phase).toBe('idle');
  });
});

/** A match of the draft in `itemId`: its sentence starts at `sentenceStart`, the match `matchStart` into it. */
const at = (
  itemId: string,
  sentenceStart: number,
  matchStart: number,
  sentence = 'Pug ran far.',
) => ({
  itemId,
  chapterId: 'c1',
  chapterTitle: null,
  speaker: null,
  hasAudio: false,
  sentence,
  sentenceStart,
  matchStart,
  matchLength: 3,
  spokenSentence: sentence,
  spokenMatchStart: matchStart,
  spokenMatchLength: 3,
});

describe('the sentence the editor was opened from', () => {
  const matches = [at('i1', 0, 0), at('i2', 0, 4), at('i2', 20, 2), at('i2', 20, 8)];

  it('is the draft match at that place', () => {
    expect(originMatch(matches, { itemId: 'i2', offset: 28 })).toBe(matches[3]!);
  });

  it('is the match in the same sentence when the draft no longer matches at that very place', () => {
    expect(originMatch(matches, { itemId: 'i2', offset: 25 })).toBe(matches[2]!);
  });

  it('is none when the draft matches nowhere in that sentence', () => {
    expect(originMatch(matches, { itemId: 'i3', offset: 0 })).toBeNull();
    expect(originMatch([at('i2', 0, 4)], { itemId: 'i2', offset: 20 })).toBeNull();
  });
});
