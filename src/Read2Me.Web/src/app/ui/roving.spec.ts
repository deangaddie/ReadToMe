import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { FakeTimers } from '../../testing/fake-timers';
import { TypeAhead, rovingKeydown, rovingTarget, typeAheadTarget } from './roving';

afterEach(() => document.body.replaceChildren());

describe('rovingTarget', () => {
  it('moves by arrows along the orientation, wrapping at the ends', () => {
    expect(rovingTarget('ArrowRight', 0, 3, 'horizontal')).toBe(1);
    expect(rovingTarget('ArrowRight', 2, 3, 'horizontal')).toBe(0);
    expect(rovingTarget('ArrowLeft', 0, 3, 'horizontal')).toBe(2);
    expect(rovingTarget('ArrowDown', 1, 3, 'vertical')).toBe(2);
    expect(rovingTarget('ArrowUp', 0, 3, 'vertical')).toBe(2);
  });

  it('ignores the cross axis and non-roving keys', () => {
    expect(rovingTarget('ArrowDown', 0, 3, 'horizontal')).toBeNull();
    expect(rovingTarget('ArrowRight', 0, 3, 'vertical')).toBeNull();
    expect(rovingTarget('Enter', 0, 3, 'horizontal')).toBeNull();
    expect(rovingTarget('ArrowRight', 0, 0, 'horizontal')).toBeNull();
  });

  it('jumps to the ends with Home and End', () => {
    expect(rovingTarget('Home', 2, 3, 'horizontal')).toBe(0);
    expect(rovingTarget('End', 0, 3, 'vertical')).toBe(2);
  });
});

describe('rovingKeydown', () => {
  function mount() {
    const list = document.createElement('div');
    list.innerHTML =
      '<button tabindex="0">a</button><button tabindex="-1">b</button><button tabindex="-1">c</button>';
    document.body.append(list);
    const items = Array.from(list.querySelectorAll('button'));
    return { list, items };
  }

  it('moves focus and the single tab stop to the target item', () => {
    const { items } = mount();
    items[0]!.focus();
    const event = new KeyboardEvent('keydown', { key: 'ArrowRight', cancelable: true });
    const moved = rovingKeydown(event, items, 'horizontal');
    expect(moved).toBe(items[1]!);
    expect(document.activeElement).toBe(items[1]!);
    expect(items.map((b) => b.tabIndex)).toEqual([-1, 0, -1]);
    expect(event.defaultPrevented).toBe(true);
  });

  it('starts from the focused item, not the first', () => {
    const { items } = mount();
    items[2]!.focus();
    expect(
      rovingKeydown(new KeyboardEvent('keydown', { key: 'ArrowLeft' }), items, 'horizontal'),
    ).toBe(items[1]!);
  });

  it('leaves other keys alone', () => {
    const { items } = mount();
    items[0]!.focus();
    const event = new KeyboardEvent('keydown', { key: 'Tab', cancelable: true });
    expect(rovingKeydown(event, items, 'horizontal')).toBeNull();
    expect(event.defaultPrevented).toBe(false);
    expect(items.map((b) => b.tabIndex)).toEqual([0, -1, -1]);
  });
});

describe('typeAheadTarget', () => {
  const labels = ['Alpha', 'Beta', 'Bravo', 'alpine'];

  it('finds the first label starting with the query, case-insensitively, after the current item', () => {
    expect(typeAheadTarget('b', labels, 0)).toBe(1);
    expect(typeAheadTarget('B', labels, 1)).toBe(2);
    expect(typeAheadTarget('br', labels, 0)).toBe(2);
  });

  it('wraps round and comes back to the current item last', () => {
    expect(typeAheadTarget('al', labels, 3)).toBe(0);
    expect(typeAheadTarget('alp', labels, 0)).toBe(3);
    expect(typeAheadTarget('beta', labels, 1)).toBe(1);
  });

  it('answers null for no match, an empty query or an empty list', () => {
    expect(typeAheadTarget('z', labels, 0)).toBeNull();
    expect(typeAheadTarget('', labels, 0)).toBeNull();
    expect(typeAheadTarget('a', [], 0)).toBeNull();
  });
});

describe('TypeAhead', () => {
  let timers: FakeTimers;
  beforeEach(() => {
    timers = new FakeTimers().install();
  });
  afterEach(() => timers.restore());

  const key = (k: string, init: KeyboardEventInit = {}) =>
    new KeyboardEvent('keydown', { key: k, cancelable: true, ...init });

  it('collects printable keys and hands the query over once typing pauses for 200 ms', async () => {
    const queries: string[] = [];
    const typeAhead = new TypeAhead((q) => queries.push(q));
    expect(typeAhead.keydown(key('b'))).toBe(true);
    await timers.advance(150);
    expect(typeAhead.keydown(key('r'))).toBe(true);
    await timers.advance(150);
    expect(queries).toEqual([]);
    await timers.advance(50);
    expect(queries).toEqual(['br']);
    // The next letter starts a fresh query.
    typeAhead.keydown(key('a'));
    await timers.advance(200);
    expect(queries).toEqual(['br', 'a']);
  });

  it('leaves space, modified keys and named keys alone', () => {
    const typeAhead = new TypeAhead(() => {});
    expect(typeAhead.keydown(key(' '))).toBe(false);
    expect(typeAhead.keydown(key('a', { ctrlKey: true }))).toBe(false);
    expect(typeAhead.keydown(key('a', { metaKey: true }))).toBe(false);
    expect(typeAhead.keydown(key('ArrowDown'))).toBe(false);
    expect(typeAhead.keydown(key('Enter'))).toBe(false);
  });

  it('consumes the key, so the widget does not also act on it', () => {
    const typeAhead = new TypeAhead(() => {});
    const event = key('a');
    typeAhead.keydown(event);
    expect(event.defaultPrevented).toBe(true);
  });

  it('cancel drops the pending query', async () => {
    const queries: string[] = [];
    const typeAhead = new TypeAhead((q) => queries.push(q));
    typeAhead.keydown(key('a'));
    typeAhead.cancel();
    await timers.advance(300);
    expect(queries).toEqual([]);
  });
});
