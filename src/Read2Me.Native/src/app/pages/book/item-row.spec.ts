import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { ParagraphItemDto } from '@app/api';
import { itemRow } from './item-row';
import type { RowContext } from './reader-rows';

const NARRATOR_ID = '00000000-0000-0000-0000-000000000001';

function item(id: string, overrides: Partial<ParagraphItemDto> = {}): ParagraphItemDto {
  return {
    id,
    itemType: 'Character',
    text: `text ${id}`,
    characterId: null,
    audioFileName: null,
    voiceInstructions: null,
    orderKey: id,
    isPause: false,
    ...overrides,
  };
}

const NARRATION = item('n', { itemType: 'Narration', characterId: NARRATOR_ID });
const DIALOG = item('d', { characterId: 'h', audioFileName: 'audio/d.wav' });

function ctx(overrides: Partial<RowContext> = {}): RowContext {
  return {
    folder: 'dune',
    mode: 'audio',
    speakers: {
      names: { h: 'Hardin' },
      narrator: { characterId: NARRATOR_ID, displayName: 'Narrator', isLinked: false },
    },
    paragraphStatus: {},
    itemStatus: {},
    voices: {},
    reviews: {},
    selectable: false,
    selected: new Set(),
    itemSelectable: true,
    selectedItems: new Set(),
    narratorOnlyMode: false,
    ancestry: { c1: { partId: 'pt1', volumeId: 'v1' } },
    roster: [],
    ...overrides,
  };
}

async function mount(context: RowContext, row: ParagraphItemDto) {
  const host = document.createElement('div');
  document.body.append(host);
  render(itemRow(row, 'p1', context, { isFirst: true, isLast: true }), host);
  await Promise.resolve();
  await Promise.resolve();
  return host.firstElementChild as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

afterEach(() => document.body.replaceChildren());

describe('itemRow (audio)', () => {
  it('carries the item id and tints a selected row', async () => {
    const el = await mount(ctx(), NARRATION);
    expect(el.dataset['itemId']).toBe('n');
    expect(el.classList.contains('r2m-item--selected')).toBe(false);
    const selected = await mount(ctx({ selectedItems: new Set(['n']) }), NARRATION);
    expect(selected.classList.contains('r2m-item--selected')).toBe(true);
  });

  it('a settled failure shows its reason as a tooltip; a queued item shows Queued and a lock', async () => {
    const el = await mount(
      ctx({ itemStatus: { n: { outcome: { kind: 'Failed', reason: 'No default voice' } } } }),
      NARRATION,
    );
    const chip = el.querySelector('.r2m-status-chip')!;
    expect(text(chip)).toContain('Failed');
    expect(chip.getAttribute('data-tooltip')).toBe('No default voice');
    expect(el.querySelector('.r2m-item__lock')).toBeNull();

    const queued = await mount(
      ctx({ itemStatus: { n: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
      NARRATION,
    );
    expect(text(queued.querySelector('.r2m-status-chip'))).toContain('Queued');
    expect(queued.querySelector('.r2m-item__lock')).not.toBeNull();
  });

  it('outside Audio mode the item queue state is not shown', async () => {
    const el = await mount(
      ctx({ mode: 'speakers', itemSelectable: false, itemStatus: { n: { status: 'Queued' } } }),
      NARRATION,
    );
    expect(el.querySelector('.r2m-status-chip')).toBeNull();
    expect(el.querySelector('.r2m-item__lock')).toBeNull();
    expect(el.querySelector('[data-testid=voice-line]')).toBeNull();
  });

  describe('review chip states', () => {
    const review = {
      state: 'NeedsReview' as const,
      normalizeOk: true,
      normalizeReason: null,
      verifyOk: false,
      wer: 0.3,
      verifyReason: 'transcript drifted',
      transcript: null,
      originalTextSnapshot: null,
    };

    it('a verify failure is a warn chip', async () => {
      const el = await mount(ctx({ reviews: { d: review } }), DIALOG);
      const chip = el.querySelector('.r2m-status-chip')!;
      expect(text(chip)).toContain('Verify failed');
      expect(chip.classList.contains('r2m-status-chip--warn')).toBe(true);
      expect(chip.getAttribute('data-tooltip')).toBe('WER 30% — transcript drifted');
    });

    it('a normalize failure is an error chip', async () => {
      const el = await mount(
        ctx({ reviews: { d: { ...review, normalizeOk: false, normalizeReason: 'clipped' } } }),
        DIALOG,
      );
      const chip = el.querySelector('.r2m-status-chip')!;
      expect(text(chip)).toContain('Normalize failed');
      expect(chip.classList.contains('r2m-status-chip--error')).toBe(true);
    });

    it('a dismissed review is a faded icon with no chip', async () => {
      const el = await mount(ctx({ reviews: { d: { ...review, state: 'Dismissed' } } }), DIALOG);
      expect(el.querySelector('.r2m-status-chip')).toBeNull();
      expect(el.querySelector('[data-testid=review-dismissed]')).not.toBeNull();
    });

    it('the hub review wins over the REST map: a null hub review clears the chip', async () => {
      const el = await mount(
        ctx({ reviews: { d: review }, itemStatus: { d: { audioVersion: 2, review: null } } }),
        DIALOG,
      );
      expect(el.querySelector('.r2m-status-chip')).toBeNull();
    });
  });
});
