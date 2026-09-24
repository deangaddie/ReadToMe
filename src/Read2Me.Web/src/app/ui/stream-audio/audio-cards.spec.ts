import { AudioGenEvent } from '@app/live/hub-events';
import { foldAudioCards } from './audio-cards';

const started = (id = 'i1', attempt = 1): AudioGenEvent => ({
  kind: 'itemStarted',
  id,
  attempt,
  character: 'Hardin',
  text: 'The Encyclopedia comes first.',
});

describe('foldAudioCards', () => {
  it('opens a card on itemStarted with Generate active and the rest pending', () => {
    const [card] = foldAudioCards([started()]);
    expect(card?.key).toBe('i1#1');
    expect(card?.character).toBe('Hardin');
    expect(card?.phases.map((p) => `${p.id}:${p.state}`)).toEqual([
      'generate:active',
      'normalize:pending',
      'postProcess:pending',
      'transcribe:pending',
      'verify:pending',
    ]);
  });

  it('walks the five phases through a happy path', () => {
    const [card] = foldAudioCards([
      started(),
      { kind: 'audioGenerated', id: 'i1', attempt: 1 },
      { kind: 'normalized', id: 'i1', attempt: 1, ok: true },
      { kind: 'postProcessed', id: 'i1', attempt: 1, stepId: 'silence-trim', applied: true },
      {
        kind: 'postProcessed',
        id: 'i1',
        attempt: 1,
        stepId: 'denoise',
        applied: false,
        reason: 'disabled',
      },
      { kind: 'transcribed', id: 'i1', attempt: 1, transcript: 'the encyclopedia comes first' },
      { kind: 'verified', id: 'i1', attempt: 1, ok: true, wer: 0.05, rescued: false },
    ]);
    expect(card?.phases.map((p) => p.state)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok']);
    expect(card?.phases[2]?.lines).toEqual([
      'silence-trim: applied',
      'denoise: skipped — disabled',
    ]);
    expect(card?.phases[3]?.lines).toEqual(['the encyclopedia comes first']);
    expect(card?.phases[4]?.lines).toEqual(['WER 5.0%']);
    expect(card?.failed).toBe(false);
  });

  it('flags warn on failed normalize / rescued verify and error on failed verify', () => {
    const [a, b] = foldAudioCards([
      started('a'),
      { kind: 'audioGenerated', id: 'a', attempt: 1 },
      { kind: 'normalized', id: 'a', attempt: 1, ok: false, reason: 'too quiet' },
      {
        kind: 'verified',
        id: 'a',
        attempt: 1,
        ok: true,
        wer: 0.2,
        rescued: true,
        reason: 'semantic match',
      },
      started('b'),
      { kind: 'verified', id: 'b', attempt: 1, ok: false, wer: 0.6, rescued: false },
    ]);
    expect(a?.phases[1]?.state).toBe('warn');
    expect(a?.phases[1]?.lines).toEqual(['too quiet']);
    expect(a?.phases[4]?.state).toBe('warn');
    expect(a?.phases[4]?.lines).toEqual(['WER 20.0% · rescued · semantic match']);
    // Skipped intermediate phases are filled in as ok once a later phase reports.
    expect(a?.phases.slice(2, 4).map((p) => p.state)).toEqual(['ok', 'ok']);
    expect(b?.phases[4]?.state).toBe('error');
  });

  it('marks the active phase as error on failed and keeps the reason', () => {
    const [card] = foldAudioCards([
      started(),
      { kind: 'audioGenerated', id: 'i1', attempt: 1 },
      { kind: 'failed', id: 'i1', attempt: 1, reason: 'TTS 500' },
    ]);
    expect(card?.failed).toBe(true);
    expect(card?.reason).toBe('TTS 500');
    expect(card?.phases[1]?.state).toBe('error');
    expect(card?.phases[2]?.state).toBe('pending');
  });

  it('keeps attempts apart, ignores events for unknown items, and caps the card count', () => {
    const events: AudioGenEvent[] = [
      { kind: 'audioGenerated', id: 'ghost', attempt: 1 },
      started('x', 1),
      { kind: 'failed', id: 'x', attempt: 1, reason: 'boom' },
      started('x', 2),
      started('y', 1),
      started('z', 1),
    ];
    const cards = foldAudioCards(events, 2);
    expect(cards.map((c) => c.key)).toEqual(['y#1', 'z#1']);
    const all = foldAudioCards(events);
    expect(all.map((c) => c.key)).toEqual(['x#1', 'x#2', 'y#1', 'z#1']);
    expect(all[0]?.failed).toBe(true);
    expect(all[1]?.failed).toBe(false);
  });
});
