import { describe, expect, it } from 'bun:test';
import { type Block, STALE_READ_ONLY, VIEWING_READ_ONLY } from '@app/api';
import {
  BULK_WAITS_REASON,
  type ItemBarInputs,
  type ParagraphBarInputs,
  itemBarGates,
  paragraphBarGates,
} from './selection-bar-gates';

const VIEWING: Block = { reason: VIEWING_READ_ONLY };
const STALE: Block = { reason: STALE_READ_ONLY };
const SAVING: Block = { reason: null };

/** Nothing blocks the editor, no selection intent is in flight, the attribution queue is idle. */
const OPEN_PARAGRAPHS: ParagraphBarInputs = { block: null, working: false, queueBusy: false };
const OPEN_ITEMS: ItemBarInputs = { block: null, working: false, generating: false };

describe('paragraphBarGates (the paragraph selection bar)', () => {
  it('opens Attribute and Bulk assign when nothing blocks them', () => {
    expect(paragraphBarGates(OPEN_PARAGRAPHS)).toEqual({ attribute: null, bulkAssign: null });
  });

  it.each<[string, Partial<ParagraphBarInputs>]>([
    ['a save is in flight', { block: SAVING }],
    ['a selection intent is in flight', { working: true }],
  ])('blocks both without a reason while %s', (_, change) => {
    expect(paragraphBarGates({ ...OPEN_PARAGRAPHS, ...change })).toEqual({
      attribute: { reason: null },
      bulkAssign: { reason: null },
    });
  });

  it.each<[string, Block]>([
    ['a History step is viewed', VIEWING],
    ['the view is stale', STALE],
  ])('blocks both with the reason while %s', (_, block) => {
    expect(paragraphBarGates({ ...OPEN_PARAGRAPHS, block })).toEqual({
      attribute: block,
      bulkAssign: block,
    });
  });

  it('blocks only Bulk assign, with its own reason, while attribution runs', () => {
    expect(paragraphBarGates({ ...OPEN_PARAGRAPHS, queueBusy: true })).toEqual({
      attribute: null,
      bulkAssign: { reason: BULK_WAITS_REASON },
    });
    expect(BULK_WAITS_REASON).toBe('Bulk assign waits until attribution has finished');
  });

  it("gives Bulk assign's wait a reason where Attribute's busy has none", () => {
    for (const change of [{ block: SAVING }, { working: true }]) {
      expect(paragraphBarGates({ ...OPEN_PARAGRAPHS, ...change, queueBusy: true })).toEqual({
        attribute: { reason: null },
        bulkAssign: { reason: BULK_WAITS_REASON },
      });
    }
  });

  it.each<[string, Block]>([
    ['viewing', VIEWING],
    ['stale', STALE],
  ])("ranks %s above Bulk assign's own wait", (_, block) => {
    expect(paragraphBarGates({ block, working: true, queueBusy: true })).toEqual({
      attribute: block,
      bulkAssign: block,
    });
  });
});

describe('itemBarGates (the item selection bar)', () => {
  it('opens Generate audio when nothing blocks it', () => {
    expect(itemBarGates(OPEN_ITEMS)).toEqual({ generate: null });
  });

  it.each<[string, Partial<ItemBarInputs>]>([
    ['a save is in flight', { block: SAVING }],
    ['a selection intent is in flight', { working: true }],
    ['an audio preflight or enqueue is in flight', { generating: true }],
  ])('blocks Generate audio without a reason while %s', (_, change) => {
    expect(itemBarGates({ ...OPEN_ITEMS, ...change })).toEqual({ generate: { reason: null } });
  });

  it.each<[string, Block]>([
    ['a History step is viewed', VIEWING],
    ['the view is stale', STALE],
  ])('blocks Generate audio with the reason while %s, even while generating', (_, block) => {
    expect(itemBarGates({ block, working: true, generating: true })).toEqual({ generate: block });
  });
});
