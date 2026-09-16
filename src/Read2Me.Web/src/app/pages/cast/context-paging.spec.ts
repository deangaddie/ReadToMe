import {
  INITIAL_WINDOW,
  canGrowAfter,
  canGrowBefore,
  contextSpeakers,
  growAfter,
  growBefore,
} from './context-paging';

describe('context window', () => {
  it('opens at 3 before / 2 after', () => {
    expect(INITIAL_WINDOW).toEqual({ before: 3, after: 2 });
  });

  it('grows before by 3 and caps at 10', () => {
    let w = INITIAL_WINDOW;
    w = growBefore(w);
    expect(w.before).toBe(6);
    w = growBefore(w);
    expect(w.before).toBe(9);
    expect(canGrowBefore(w)).toBe(true);
    w = growBefore(w);
    expect(w.before).toBe(10);
    expect(canGrowBefore(w)).toBe(false);
    expect(growBefore(w).before).toBe(10);
    expect(w.after).toBe(2);
  });

  it('grows after by 2 and caps at 10', () => {
    let w = INITIAL_WINDOW;
    for (let i = 0; i < 4; i++) w = growAfter(w);
    expect(w.after).toBe(10);
    expect(canGrowAfter(w)).toBe(false);
    expect(growAfter(w).after).toBe(10);
    expect(w.before).toBe(3);
  });
});

describe('contextSpeakers', () => {
  it('lists distinct dialog speakers in order, unknown as "?", narration as none', () => {
    const speakers = contextSpeakers({
      text: 'x',
      items: [
        { itemId: '1', text: 'a', isDialog: false, speaker: null },
        { itemId: '2', text: 'b', isDialog: true, speaker: 'Alice' },
        { itemId: '3', text: 'c', isDialog: true, speaker: null },
        { itemId: '4', text: 'd', isDialog: true, speaker: 'Alice' },
        { itemId: '5', text: 'e', isDialog: true, speaker: null },
      ],
    });
    expect(speakers).toEqual([
      { name: 'Alice', unknown: false },
      { name: '?', unknown: true },
    ]);
    expect(
      contextSpeakers({ text: 'n', items: [{ itemId: '1', text: 'a', isDialog: false, speaker: null }] }),
    ).toEqual([]);
  });
});
