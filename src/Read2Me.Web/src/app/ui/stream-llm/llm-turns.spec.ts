import { LlmStreamEvent } from '@app/live/hub-events';
import { LlmTurn, foldLlmTurns } from './llm-turns';

const start = (preview: string, configName = 'gemma-26b'): LlmStreamEvent => ({
  kind: 'requestStarted',
  paragraphPreview: preview,
  prompt: `PROMPT ${preview}`,
  configId: 'c1',
  configName,
});

describe('foldLlmTurns', () => {
  it('opens a turn on requestStarted and appends deltas to it', () => {
    const rows = foldLlmTurns([
      start('Hardin leaned back'),
      { kind: 'thinkingDelta', text: 'Who ' },
      { kind: 'thinkingDelta', text: 'speaks?' },
      { kind: 'contentDelta', text: '{"speaker":' },
      { kind: 'contentDelta', text: '"Hardin"}' },
    ]);
    expect(rows.length).toBe(1);
    const turn = rows[0] as LlmTurn;
    expect(turn.kind).toBe('turn');
    expect(turn.state).toBe('open');
    expect(turn.paragraphPreview).toBe('Hardin leaned back');
    expect(turn.prompt).toBe('PROMPT Hardin leaned back');
    expect(turn.thinking).toBe('Who speaks?');
    expect(turn.response).toBe('{"speaker":"Hardin"}');
  });

  it('closes turns with completed stats, failure reason or abort', () => {
    const rows = foldLlmTurns([
      start('a'),
      {
        kind: 'streamCompleted',
        tokensIn: 100,
        tokensOut: 20,
        generationMs: 1500,
        tokensPerSecond: 13.33,
      },
      start('b'),
      { kind: 'streamFailed', reason: 'timeout' },
      start('c'),
      { kind: 'streamAborted', tokensOut: 5, generationMs: 200, tokensPerSecond: 25 },
    ]) as LlmTurn[];
    expect(rows.map((r) => r.state)).toEqual(['completed', 'failed', 'aborted']);
    expect(rows[0]).toMatchObject({ tokensIn: 100, tokensOut: 20, generationMs: 1500 });
    expect(rows[1]?.reason).toBe('timeout');
    expect(rows[2]).toMatchObject({ tokensOut: 5, tokensPerSecond: 25 });
  });

  it('ignores deltas and completions that arrive without an open turn', () => {
    const rows = foldLlmTurns([
      { kind: 'contentDelta', text: 'orphan' },
      { kind: 'streamCompleted' },
      start('a'),
      { kind: 'streamCompleted' },
      { kind: 'contentDelta', text: 'late' },
    ]) as LlmTurn[];
    expect(rows.length).toBe(1);
    expect(rows[0]?.response).toBe('');
  });

  it('inserts marker rows for run boundaries and escalation', () => {
    const rows = foldLlmTurns([
      { kind: 'runStarted' },
      { kind: 'escalationStarted', step: 2, configName: 'qwen-28b', itemCount: 3 },
      { kind: 'escalationStarted', step: 3, configName: 'qwen-28b', itemCount: 1 },
      { kind: 'runEnded' },
    ]);
    expect(rows.map((r) => (r.kind === 'marker' ? r.text : 'turn'))).toEqual([
      'Run started',
      'Escalating 3 items → qwen-28b (step 2)',
      'Escalating 1 item → qwen-28b (step 3)',
      'Run ended',
    ]);
  });

  it('keeps only the last maxTurns turns, dropping older markers with them', () => {
    const events: LlmStreamEvent[] = [{ kind: 'runStarted' }];
    for (let i = 0; i < 5; i++) {
      events.push(start(`t${i}`), { kind: 'streamCompleted' });
    }
    const rows = foldLlmTurns(events, 2);
    expect(rows.length).toBe(2);
    expect((rows[0] as LlmTurn).paragraphPreview).toBe('t3');
    expect((rows[1] as LlmTurn).paragraphPreview).toBe('t4');
  });

  it('assigns stable seq keys from event position', () => {
    const rows = foldLlmTurns([{ kind: 'runStarted' }, start('a')]);
    expect(rows.map((r) => r.seq)).toEqual([0, 1]);
  });
});
