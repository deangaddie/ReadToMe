import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { TreeLayout } from './tree-layout';

beforeEach(() => localStorage.clear());
afterEach(() => localStorage.clear());

/**
 * Runs `body` with `localStorage` broken as a browser that blocks storage breaks it: reading the
 * global throws, or (`'write'`) every write does. happy-dom's Storage ignores method spies.
 */
function withBrokenStorage(broken: 'read' | 'write', body: () => void): void {
  const real = localStorage;
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
  const fail = () => {
    throw new DOMException('storage blocked', 'SecurityError');
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    get:
      broken === 'read'
        ? fail
        : () => ({ getItem: (key: string) => real.getItem(key), setItem: fail }),
  });
  try {
    body();
  } finally {
    if (descriptor) Object.defineProperty(globalThis, 'localStorage', descriptor);
  }
}

describe('TreeLayout', () => {
  it('starts at 280px and shown', () => {
    const layout = new TreeLayout();
    expect(layout.width()).toBe(280);
    expect(layout.open()).toBe(true);
  });

  it('remembers the width and Show/Hide for the next page load', () => {
    const layout = new TreeLayout();
    layout.setWidth(360);
    layout.setOpen(false);
    const next = new TreeLayout();
    expect(next.width()).toBe(360);
    expect(next.open()).toBe(false);
  });

  it('falls back to 280px and shown when storage cannot be read', () => {
    localStorage.setItem('r2m.tree.width', '400');
    localStorage.setItem('r2m.tree.open', '0');
    withBrokenStorage('read', () => {
      const layout = new TreeLayout();
      expect(layout.width()).toBe(280);
      expect(layout.open()).toBe(true);
    });
  });

  it('keeps a change in memory when storage cannot be written', () => {
    withBrokenStorage('write', () => {
      const layout = new TreeLayout();
      layout.setWidth(400);
      layout.setOpen(false);
      expect(layout.width()).toBe(400);
      expect(layout.open()).toBe(false);
    });
  });

  it('caps the width at 480px, and leaves the reader 450px beside it', () => {
    const layout = new TreeLayout();
    expect(layout.max()).toBe(480);
    layout.setBodyWidth(1400);
    expect(layout.max()).toBe(480);
    // 900 body − 16 gap − 450 reader.
    layout.setBodyWidth(900);
    expect(layout.max()).toBe(434);
  });

  it('shows a saved width wider than the max clamped, and restores it when the body widens', () => {
    const layout = new TreeLayout();
    layout.setBodyWidth(1400);
    layout.setWidth(460);
    layout.setBodyWidth(800);
    expect(layout.width()).toBe(334);
    // A narrow window never saves: a reload still has the user's width.
    expect(new TreeLayout().width()).toBe(460);
    layout.setBodyWidth(1200);
    expect(layout.width()).toBe(460);
  });

  it('never shows the tree narrower than 220px, even when the reader gets less', () => {
    const layout = new TreeLayout();
    layout.setBodyWidth(600);
    expect(layout.width()).toBe(220);
    expect(layout.max()).toBe(220);
  });

  it('ignores a stored width that is not a number', () => {
    localStorage.setItem('r2m.tree.width', 'wide');
    expect(new TreeLayout().width()).toBe(280);
  });
});
