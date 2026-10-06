/**
 * Angular-shaped signals over `@preact/signals-core` (renderer-and-signals research). Call sites
 * keep `signal()/.set/.update/.asReadonly`, `computed()`, `effect(onCleanup)` and `untracked`, so
 * ported stores barely change, and swapping in native signals later touches only this file.
 *
 * Difference from Angular: an effect runs synchronously on the write that dirties it, not on the
 * next change-detection pass. Writes that must land together go inside `batch()`.
 */
import {
  batch,
  computed as preactComputed,
  effect as preactEffect,
  signal as preactSignal,
  untracked as preactUntracked,
} from '@preact/signals-core';

export type ReadonlySignal<T> = () => T;

export interface WritableSignal<T> extends ReadonlySignal<T> {
  set(value: T): void;
  update(change: (value: T) => T): void;
  asReadonly(): ReadonlySignal<T>;
}

export function signal<T>(initial: T): WritableSignal<T> {
  const s = preactSignal(initial);
  const read = (() => s.value) as WritableSignal<T>;
  read.set = (value) => {
    s.value = value;
  };
  read.update = (change) => {
    s.value = change(s.peek());
  };
  read.asReadonly = () => () => s.value;
  return read;
}

export function computed<T>(derive: () => T): ReadonlySignal<T> {
  const c = preactComputed(derive);
  return () => c.value;
}

export type EffectCleanupRegister = (cleanup: () => void) => void;

/** Runs now and again whenever a signal it read changes; returns the disposer. */
export function effect(run: (onCleanup: EffectCleanupRegister) => void): () => void {
  return preactEffect(() => {
    const cleanups: (() => void)[] = [];
    run((cleanup) => cleanups.push(cleanup));
    return cleanups.length ? () => cleanups.forEach((c) => c()) : undefined;
  });
}

/** Reads without tracking; accepts a signal or any function, like Angular's. */
export function untracked<T>(read: () => T): T {
  return preactUntracked(read);
}

export { batch };
