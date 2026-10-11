import { describe, expect, it } from 'bun:test';
import { batch, computed, effect, signal, untracked } from './signals';

describe('signal', () => {
  it('reads by call, and writes through set and update', () => {
    const count = signal(1);
    expect(count()).toBe(1);
    count.set(5);
    count.update((n) => n + 1);
    expect(count()).toBe(6);
  });

  it('asReadonly follows the source and offers no way to write', () => {
    const count = signal(1);
    const readonly = count.asReadonly();
    count.set(2);
    expect(readonly()).toBe(2);
    expect('set' in readonly).toBe(false);
  });
});

describe('computed', () => {
  it('derives from signals and recomputes only when read after a change', () => {
    const a = signal(2);
    const b = signal(3);
    let runs = 0;
    const product = computed(() => {
      runs++;
      return a() * b();
    });
    expect(product()).toBe(6);
    expect(product()).toBe(6);
    expect(runs).toBe(1);
    a.set(4);
    expect(product()).toBe(12);
    expect(runs).toBe(2);
  });
});

describe('effect', () => {
  it('runs at once, and synchronously on each write to a signal it read', () => {
    const name = signal('a');
    const seen: string[] = [];
    effect(() => {
      seen.push(name());
    });
    name.set('b');
    expect(seen).toEqual(['a', 'b']);
  });

  it('runs its cleanups before the next run and on dispose', () => {
    const name = signal('a');
    const log: string[] = [];
    const dispose = effect((onCleanup) => {
      const value = name();
      log.push(`run ${value}`);
      onCleanup(() => log.push(`cleanup ${value}`));
    });
    name.set('b');
    dispose();
    expect(log).toEqual(['run a', 'cleanup a', 'run b', 'cleanup b']);
  });

  it('stops running once disposed', () => {
    const name = signal('a');
    let runs = 0;
    const dispose = effect(() => {
      name();
      runs++;
    });
    dispose();
    name.set('b');
    expect(runs).toBe(1);
  });

  it('batch lands several writes as one run', () => {
    const a = signal(1);
    const b = signal(1);
    const seen: number[] = [];
    effect(() => {
      seen.push(a() + b());
    });
    batch(() => {
      a.set(2);
      b.set(2);
    });
    expect(seen).toEqual([2, 4]);
  });
});

describe('untracked', () => {
  it('reads a signal without subscribing the effect to it', () => {
    const tracked = signal(1);
    const ignored = signal(1);
    const seen: number[] = [];
    effect(() => {
      seen.push(tracked() + untracked(ignored));
    });
    ignored.set(10);
    expect(seen).toEqual([2]);
    tracked.set(2);
    expect(seen).toEqual([2, 12]);
  });
});
