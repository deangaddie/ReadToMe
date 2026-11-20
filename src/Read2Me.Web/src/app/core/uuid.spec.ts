import { describe, expect, it } from 'bun:test';
import { newUuid } from './uuid';

const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newUuid', () => {
  it('uses randomUUID where the context has it', () => {
    expect(
      newUuid({
        randomUUID: () => 'from-randomUUID',
        getRandomValues: crypto.getRandomValues.bind(crypto),
      } as never),
    ).toBe('from-randomUUID');
  });

  it('builds a v4 UUID from getRandomValues on an insecure context (no randomUUID)', () => {
    const insecure = { getRandomValues: crypto.getRandomValues.bind(crypto) };
    const ids = new Set(Array.from({ length: 50 }, () => newUuid(insecure)));
    for (const id of ids) expect(id).toMatch(V4);
    expect(ids.size).toBe(50);
  });

  it('sets the version and variant bits whatever the random bytes are', () => {
    const allOnes = {
      getRandomValues: <T extends ArrayBufferView>(a: T) => (
        new Uint8Array(a.buffer).fill(0xff),
        a
      ),
    };
    expect(newUuid(allOnes as never)).toBe('ffffffff-ffff-4fff-bfff-ffffffffffff');
  });
});
