import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { override, provide, resetServices, token, use } from './services';

class Counter {
  static created = 0;
  readonly id = ++Counter.created;
}

beforeEach(() => {
  resetServices();
  Counter.created = 0;
});
afterEach(() => document.body.replaceChildren());

describe('use', () => {
  it('creates a class once and returns the same instance afterwards', () => {
    const first = use(Counter);
    expect(first).toBeInstanceOf(Counter);
    expect(use(Counter)).toBe(first);
    expect(Counter.created).toBe(1);
  });

  it('builds a token from its factory, once', () => {
    let calls = 0;
    const GREETING = token('GREETING', () => ({ text: `hello ${++calls}` }));
    expect(use(GREETING).text).toBe('hello 1');
    expect(use(GREETING).text).toBe('hello 1');
  });

  it('prefers the instance the nearest ancestor provided', () => {
    const outer = document.createElement('section');
    const inner = document.createElement('div');
    const leaf = document.createElement('span');
    outer.append(inner);
    inner.append(leaf);
    document.body.append(outer);

    const outerCounter = provide(outer, Counter, new Counter());
    expect(use(Counter, leaf)).toBe(outerCounter);

    const innerCounter = provide(inner, Counter, new Counter());
    expect(use(Counter, leaf)).toBe(innerCounter);
    expect(use(Counter, outer)).toBe(outerCounter);
  });

  it('includes the element itself in the lookup', () => {
    const host = document.createElement('div');
    const scoped = provide(host, Counter, new Counter());
    expect(use(Counter, host)).toBe(scoped);
  });

  it('falls back to the app-wide instance when no ancestor provides one', () => {
    const el = document.createElement('div');
    document.body.append(el);
    expect(use(Counter, el)).toBe(use(Counter));
  });

  it('crosses a shadow boundary to the host', () => {
    const host = document.createElement('div');
    const inside = document.createElement('span');
    host.attachShadow({ mode: 'open' }).append(inside);
    const scoped = provide(host, Counter, new Counter());
    expect(use(Counter, inside)).toBe(scoped);
  });
});

describe('override and resetServices', () => {
  it('override swaps the app-wide instance', () => {
    const fake = new Counter();
    override(Counter, fake);
    expect(use(Counter)).toBe(fake);
  });

  it('resetServices forgets every app-wide instance', () => {
    const first = use(Counter);
    resetServices();
    expect(use(Counter)).not.toBe(first);
  });
});
