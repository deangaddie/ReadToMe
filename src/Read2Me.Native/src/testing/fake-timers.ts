/**
 * Manual timers for specs that reason about delays (`vi.useFakeTimers` has no bun:test
 * counterpart). `install()` replaces the global `setTimeout`/`clearTimeout`; `advance(ms)` runs
 * every timer due by then in order, letting microtasks settle between them; `restore()` puts the
 * real timers back. Nested timers scheduled while advancing run when their own time comes.
 */
export class FakeTimers {
  #now = 0;
  #nextId = 1;
  #timers = new Map<number, { at: number; run: () => void }>();
  #real: { setTimeout: typeof setTimeout; clearTimeout: typeof clearTimeout } | null = null;

  install(): this {
    this.#real = { setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
    globalThis.setTimeout = ((run: () => void, ms = 0) => {
      const id = this.#nextId++;
      this.#timers.set(id, { at: this.#now + Math.max(0, ms), run });
      return id;
    }) as unknown as typeof setTimeout;
    globalThis.clearTimeout = ((id: number) => {
      this.#timers.delete(id);
    }) as unknown as typeof clearTimeout;
    return this;
  }

  restore(): void {
    if (!this.#real) return;
    globalThis.setTimeout = this.#real.setTimeout;
    globalThis.clearTimeout = this.#real.clearTimeout;
    this.#real = null;
    this.#timers.clear();
  }

  /** Pending timers, soonest first. */
  get pending(): number {
    return this.#timers.size;
  }

  async advance(ms: number): Promise<void> {
    const until = this.#now + ms;
    for (;;) {
      const next = this.#soonest();
      if (!next || next.timer.at > until) break;
      this.#now = next.timer.at;
      this.#timers.delete(next.id);
      next.timer.run();
      await settleMicrotasks();
    }
    this.#now = until;
  }

  #soonest(): { id: number; timer: { at: number; run: () => void } } | null {
    let best: { id: number; timer: { at: number; run: () => void } } | null = null;
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
