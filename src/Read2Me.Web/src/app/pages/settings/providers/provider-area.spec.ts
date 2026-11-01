import { describe, expect, it } from 'bun:test';
import type { ProviderType } from '@app/api';
import { PARAGRAPH_TTS_AREA, providerType } from './provider-area';

describe('providerType', () => {
  it('finds a listed type', () => {
    expect(providerType(PARAGRAPH_TTS_AREA, 'Breeze').label).toBe('Breeze');
  });

  it('names a stored type no provider offers any more instead of passing it off as the first', () => {
    // A name the area no longer lists (Chatterbox Turbo was removed by ADR 0010).
    const retired = 'ChatterboxTurbo' as ProviderType;
    const removed = providerType(PARAGRAPH_TTS_AREA, retired);

    expect(removed.value).toBe(retired);
    expect(removed.label).toBe('Unknown type ChatterboxTurbo');
  });
});
