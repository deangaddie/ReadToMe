import { describe, expect, it } from 'bun:test';
import { Emitter } from './emitter';

describe('Emitter', () => {
  it('fans a value out to every subscriber, synchronously', () => {
    const emitter = new Emitter<number>();
    const seen: string[] = [];
    emitter.subscribe((n) => seen.push(`a${n}`));
    emitter.subscribe((n) => seen.push(`b${n}`));
    emitter.emit(1);
    expect(seen).toEqual(['a1', 'b1']);
  });

  it('stops delivering once the returned function is called', () => {
    const emitter = new Emitter<number>();
    const seen: number[] = [];
    const unsubscribe = emitter.subscribe((n) => seen.push(n));
    emitter.emit(1);
    unsubscribe();
    emitter.emit(2);
    expect(seen).toEqual([1]);
  });

  it('a listener added during a fan-out waits for the next value', () => {
    const emitter = new Emitter<number>();
    const late: number[] = [];
    emitter.subscribe(() => emitter.subscribe((n) => late.push(n)));
    emitter.emit(1);
    expect(late).toEqual([]);
  });
});
