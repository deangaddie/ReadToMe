import { afterEach, describe, expect, it } from 'bun:test';
import { rovingKeydown, rovingTarget } from './roving';

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
