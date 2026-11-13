import { describe, expect, it } from 'bun:test';
import { type ActionDeepLinkInput, decideActionDeepLink } from './action-deep-link';

/** A book with content, its Pipeline steps loaded, nothing viewed and nothing locking the editor. */
const READY: ActionDeepLinkInput = {
  action: 'titles',
  overview: { hasContent: true },
  pipelineLoaded: true,
  viewing: false,
  blocked: false,
};

describe('decideActionDeepLink (an Action deep link, ?action=)', () => {
  it.each<[string, Partial<ActionDeepLinkInput>]>([
    ['no ?action= parameter', { action: null }],
    ['the overview not loaded yet', { overview: null }],
    ['the Pipeline steps not loaded yet, after the overview', { pipelineLoaded: false }],
    ['the editor locked and no History step viewed', { blocked: true }],
  ])('waits with %s', (_, change) => {
    expect(decideActionDeepLink({ ...READY, ...change })).toBe('wait');
  });

  it.each(['titles', 'edit-ai', 'manual-reread'] as const)(
    'drops the parameter and opens %s, the same as its overflow entry',
    (action) => {
      expect(decideActionDeepLink({ ...READY, action })).toEqual({ open: action });
    },
  );

  it.each<[string, Partial<ActionDeepLinkInput>]>([
    ['a History step is viewed', { viewing: true }],
    ['a History step is viewed, even while the editor is locked', { viewing: true, blocked: true }],
    ['the book has no content', { overview: { hasContent: false } }],
    ['the name is unknown', { action: 'pauses' }],
  ])('only drops the parameter when %s', (_, change) => {
    expect(decideActionDeepLink({ ...READY, ...change })).toBe('drop');
  });
});
