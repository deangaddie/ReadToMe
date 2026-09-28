import { ParagraphTtsServiceType } from '@app/api';
import { PARAGRAPH_TTS_AREA, providerType } from './provider-area';

describe('providerType', () => {
  it('finds a listed type', () => {
    expect(providerType(PARAGRAPH_TTS_AREA, ParagraphTtsServiceType.Breeze).label).toBe('Breeze');
  });

  it('names a stored type no provider offers any more instead of passing it off as the first', () => {
    // 2 was Chatterbox Turbo (removed by ADR 0010); rows keep it until the data migration deletes them.
    const removed = providerType(PARAGRAPH_TTS_AREA, 2);

    expect(removed.value).toBe(2);
    expect(removed.label).toBe('Unknown type 2');
  });
});
