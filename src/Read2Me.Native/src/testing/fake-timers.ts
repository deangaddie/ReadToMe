/**
 * Manual timers for specs that reason about delays (`vi.useFakeTimers` has no bun:test
 * counterpart). `install()` replaces the global `setTimeout`/`clearTimeout`,
 * `setInterval`/`clearInterval` and `Date.now`; `advance(ms)` runs every timer due by then in
 * order, letting microtasks settle between them, and moves the clock; `restore()` puts the real
 * ones back. Nested timers scheduled while advancing run when their own time comes.
 */
interface Timer {
  at: number;
  run: () => void;
  /** Set for an interval: re-armed after each run. */
  every?: number;
}

export class FakeTimers {
  #now = 0;
  #nextId = 1;
  #timers = new Map<number, Timer>();
  #real: {
    setTimeout: typeof setTimeout;
    clearTimeout: typeof clearTimeout;
    setInterval: typeof setInterval;
    clearInterval: typeof clearInterval;
    now: typeof Date.now;
  } | null = null;

  install(): this {
    this.#real = {
      setTimeout: globalThis.setTimeout,
      clearTimeout: globalThis.clearTimeout,
      setInterval: globalThis.setInterval,
      clearInterval: globalThis.clearInterval,
      now: Date.now,
    };
    this.#now = this.#real.now();
    globalThis.setTimeout = ((run: () => void, ms = 0) =>
      this.#add({ at: this.#now + Math.max(0, ms), run })) as unknown as typeof setTimeout;
    globalThis.setInterval = ((run: () => void, ms = 0) => {
      const every = Math.max(1, ms);
      return this.#add({ at: this.#now + every, run, every });
    }) as unknown as typeof setInterval;
    const clear = ((id: number) => {
      this.#timers.delete(id);
    }) as unknown as typeof clearTimeout;
    globalThis.clearTimeout = clear;
    globalThis.clearInterval = clear as unknown as typeof clearInterval;
    Date.now = () => this.#now;
    return this;
  }

  restore(): void {
    if (!this.#real) return;
    globalThis.setTimeout = this.#real.setTimeout;
    globalThis.clearTimeout = this.#real.clearTimeout;
    globalThis.setInterval = this.#real.setInterval;
    globalThis.clearInterval = this.#real.clearInterval;
    Date.now = this.#real.now;
    this.#real = null;
    this.#timers.clear();
  }

  /** Pending timers (an interval counts once). */
  get pending(): number {
    return this.#timers.size;
  }

  /** The fake clock's `Date.now()`. */
  get now(): number {
    return this.#now;
  }

  async advance(ms: number): Promise<void> {
    const until = this.#now + ms;
    for (;;) {
      const next = this.#soonest();
      if (!next || next.timer.at > until) break;
      this.#now = next.timer.at;
      if (next.timer.every) next.timer.at += next.timer.every;
      else this.#timers.delete(next.id);
      next.timer.run();
      await settleMicrotasks();
    }
    this.#now = until;
  }

  #add(timer: Timer): number {
    const id = this.#nextId++;
    this.#timers.set(id, timer);
    return id;
  }

  #soonest(): { id: number; timer: Timer } | null {
    let best: { id: number; timer: Timer } | null = null;
    for (const [id, timer] of this.#timers) {
      if (!best || timer.at < best.timer.at || (timer.at === best.timer.at && id < best.id))
        best = { id, timer };
    }
    return best;
  }
}

/** Lets a chain of awaited promises settle (each `await` is a microtask hop). */
export async function settleMicrotasks(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}
