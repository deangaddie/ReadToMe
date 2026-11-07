import type { AudioGenMessage, LlmMessage } from '@app/api';
import { describe, expect, it } from 'bun:test';
import { AudioGenEvent, LlmStreamEvent } from '@app/live/hub-events';
import {
  appendCapped,
  mapAudioGenMessage,
  mapLlmMessage,
  startsAudioCard,
  startsLlmTurn,
} from './stream-events';
import { audioGenMessage, llmMessage } from '../../testing/live-messages';

describe('mapLlmMessage', () => {
  it('splits a delta batch into thinking and content deltas, skipping empty halves', () => {
    expect(mapLlmMessage(llmMessage({ kind: 'Delta', thinking: 'hmm', content: 'Alice' }))).toEqual(
      [
        { kind: 'thinkingDelta', text: 'hmm' },
        { kind: 'contentDelta', text: 'Alice' },
      ],
    );
    expect(mapLlmMessage(llmMessage({ kind: 'Delta', content: 'x' }))).toEqual([
      { kind: 'contentDelta', text: 'x' },
    ]);
    expect(mapLlmMessage(llmMessage({ kind: 'Delta' }))).toEqual([]);
  });

  it('maps control events field for field', () => {
    const started: LlmMessage = llmMessage({
      kind: 'RequestStarted',
      paragraphPreview: '"Hello," said Alice.',
      prompt: 'Who speaks?',
      configId: 7,
      configName: 'Gemma 26B',
    });
    expect(mapLlmMessage(started)).toEqual<LlmStreamEvent[]>([
      {
        kind: 'requestStarted',
        paragraphPreview: '"Hello," said Alice.',
        prompt: 'Who speaks?',
        configId: '7',
        configName: 'Gemma 26B',
      },
    ]);
    expect(
      mapLlmMessage(
        llmMessage({
          kind: 'StreamCompleted',
          tokensIn: 10,
          tokensOut: 20,
          generationMs: 500,
          tokensPerSecond: 40,
        }),
      ),
    ).toEqual([
      {
        kind: 'streamCompleted',
        tokensIn: 10,
        tokensOut: 20,
        generationMs: 500,
        tokensPerSecond: 40,
      },
    ]);
    expect(mapLlmMessage(llmMessage({ kind: 'StreamFailed', reason: 'boom' }))).toEqual([
      { kind: 'streamFailed', reason: 'boom' },
    ]);
    expect(mapLlmMessage(llmMessage({ kind: 'StreamAborted', tokensOut: 3 }))).toEqual([
      { kind: 'streamAborted', tokensOut: 3, generationMs: null, tokensPerSecond: null },
    ]);
    expect(
      mapLlmMessage(
        llmMessage({ kind: 'EscalationStarted', step: 2, configName: 'Qwen', itemCount: 4 }),
      ),
    ).toEqual([{ kind: 'escalationStarted', step: 2, configName: 'Qwen', itemCount: 4 }]);
    expect(mapLlmMessage(llmMessage({ kind: 'RunStarted' }))).toEqual([{ kind: 'runStarted' }]);
    expect(mapLlmMessage(llmMessage({ kind: 'RunEnded' }))).toEqual([{ kind: 'runEnded' }]);
  });
});

describe('mapAudioGenMessage', () => {
  it('maps every phase with its fields', () => {
    const base = { id: 'i1', attempt: 2 };
    const cases: [AudioGenMessage, AudioGenEvent][] = [
      [
        audioGenMessage({ kind: 'ItemStarted', ...base, character: 'Alice', text: 'Hi' }),
        { kind: 'itemStarted', ...base, character: 'Alice', text: 'Hi' },
      ],
      [audioGenMessage({ kind: 'AudioGenerated', ...base }), { kind: 'audioGenerated', ...base }],
      [
        audioGenMessage({ kind: 'Normalized', ...base, ok: false, reason: 'clipped' }),
        { kind: 'normalized', ...base, ok: false, reason: 'clipped' },
      ],
      [
        audioGenMessage({ kind: 'PostProcessed', ...base, stepId: 'trim', applied: true }),
        { kind: 'postProcessed', ...base, stepId: 'trim', applied: true, reason: null },
      ],
      [
        audioGenMessage({ kind: 'Transcribed', ...base, transcript: 'hi' }),
        { kind: 'transcribed', ...base, transcript: 'hi' },
      ],
      [
        audioGenMessage({ kind: 'Verified', ...base, ok: true, wer: 0.1, rescued: true }),
        { kind: 'verified', ...base, ok: true, wer: 0.1, reason: null, rescued: true },
      ],
      [
        audioGenMessage({ kind: 'Failed', ...base, reason: 'tts 500' }),
        { kind: 'failed', ...base, reason: 'tts 500' },
      ],
      [
        audioGenMessage({
          kind: 'ModelLoading',
          id: '00000000-0000-0000-0000-000000000000',
          attempt: 0,
          model: 'breeze-q8',
        }),
        { kind: 'modelLoading', model: 'breeze-q8' },
      ],
    ];
    for (const [wire, expected] of cases) {
      expect(mapAudioGenMessage(wire)).toEqual(expected);
    }
  });
});

describe('appendCapped', () => {
  const turn = (n: number): LlmStreamEvent => ({
    kind: 'requestStarted',
    paragraphPreview: `p${n}`,
    prompt: '',
    configId: '1',
    configName: 'c',
  });
  const delta: LlmStreamEvent = { kind: 'contentDelta', text: 'x' };

  it('keeps events from the oldest retained turn onwards', () => {
    let events: LlmStreamEvent[] = [];
    for (let n = 1; n <= 4; n++) {
      events = appendCapped(events, [turn(n), delta], startsLlmTurn, 2);
    }
    expect(events).toEqual([turn(3), delta, turn(4), delta]);
  });

  it('drops leading events that belong to no retained turn', () => {
    const events = appendCapped(
      [{ kind: 'runStarted' }, delta, turn(1), delta],
      [turn(2)],
      startsLlmTurn,
      1,
    );
    expect(events).toEqual([turn(2)]);
  });

  it('caps audio cards by itemStarted', () => {
    const start = (n: number): AudioGenEvent => ({ kind: 'itemStarted', id: `i${n}`, attempt: 1 });
    let events: AudioGenEvent[] = [];
    for (let n = 1; n <= 3; n++) events = appendCapped(events, [start(n)], startsAudioCard, 2);
    expect(events.map((e) => (e as { id: string }).id)).toEqual(['i2', 'i3']);
  });
});
