import { describe, expect, it } from 'bun:test';
import { type DeepLinkInput, deepLinkStep } from './deep-link';

/** A book with content, its Pipeline steps loaded, nothing viewed and nothing locking the editor. */
const READY: DeepLinkInput = {
  action: 'titles',
  overview: { hasContent: true },
  pipelineLoaded: true,
  viewing: false,
  blocked: false,
};

describe('deepLinkStep (?action= on the Book page)', () => {
  it.each<[string, Partial<DeepLinkInput>]>([
    ['no ?action= parameter', { action: null }],
    ['the overview not loaded yet', { overview: null }],
    ['the Pipeline steps not loaded yet, after the overview', { pipelineLoaded: false }],
    ['the editor locked and no History step viewed', { blocked: true }],
  ])('waits with %s', (_, change) => {
    expect(deepLinkStep({ ...READY, ...change })).toBe('wait');
  });

  it.each(['titles', 'edit-ai', 'manual-reread'] as const)(
    'drops the parameter and opens %s, the same as its overflow entry',
    (action) => {
      expect(deepLinkStep({ ...READY, action })).toEqual({ open: action });
    },
  );

  it.each<[string, Partial<DeepLinkInput>]>([
    ['a History step is viewed', { viewing: true }],
    ['a History step is viewed, even while the editor is locked', { viewing: true, blocked: true }],
    ['the book has no content', { overview: { hasContent: false } }],
    ['the name is unknown', { action: 'pauses' }],
  ])('only drops the parameter when %s', (_, change) => {
    expect(deepLinkStep({ ...READY, ...change })).toBe('drop');
  });
});
