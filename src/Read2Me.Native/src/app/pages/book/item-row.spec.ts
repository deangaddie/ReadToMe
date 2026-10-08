import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { render } from 'lit-html';
import type { ParagraphItemDto } from '@app/api';
import { provide } from '@app/core/services';
import { itemRow } from './item-row';
import type { RowContext } from './reader-rows';
import { AudioSelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';

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
const UNATTRIBUTED = item('u');

const ROSTER = [
  { id: NARRATOR_ID, name: 'Narrator', isNarrator: true },
  { id: 'h', name: 'Hardin' },
];

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
    locked: false,
    roster: [],
    ...overrides,
  };
}

/** The shell's page-scoped collaborators, reached by the row through DOM ancestry. */
let selection: AudioSelectionStore;
let assigner: { assign: ReturnType<typeof mock>; createAndAssign: ReturnType<typeof mock> };

async function mount(context: RowContext, row: ParagraphItemDto) {
  const host = document.createElement('div');
  provide(host, AudioSelectionStore, selection);
  provide(host, SpeakerAssigner, assigner as unknown as SpeakerAssigner);
  document.body.append(host);
  render(
    itemRow(row, { paragraphId: 'p1', chapterId: 'c1' }, context, { isFirst: true, isLast: true }),
    host,
  );
  await Promise.resolve();
  await Promise.resolve();
  return host.firstElementChild as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const box = (el: Element) => el.querySelector<HTMLInputElement>('input[type=checkbox]');

// happy-dom has no popover: the chip's menu opens on the `toggle` event the browser would fire.
const proto = HTMLElement.prototype as unknown as Record<string, () => void>;
beforeEach(() => {
  selection = new AudioSelectionStore();
  assigner = { assign: mock(async () => undefined), createAndAssign: mock(async () => undefined) };
  proto['hidePopover'] = () => undefined;
});
afterEach(() => {
  delete proto['hidePopover'];
  document.body.replaceChildren();
});

async function openChipMenu(el: Element) {
  const chip = el.querySelector('r2m-speaker-chip')!;
  chip.querySelector<HTMLButtonElement>('button')!.click();
  chip
    .querySelector('.r2m-speaker-chip__menu')!
    .dispatchEvent(Object.assign(new Event('toggle'), { newState: 'open' }));
  await chip.rendered();
  await chip.querySelector('r2m-speaker-menu')!.rendered();
  return Array.from(chip.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row'));
}

describe('itemRow (speakers)', () => {
  describe('checkbox enablement (ticket 13)', () => {
    it('narration and attributed lines can be selected; an unattributed line cannot', async () => {
      expect(box(await mount(ctx(), NARRATION))!.disabled).toBe(false);
      expect(box(await mount(ctx(), DIALOG))!.disabled).toBe(false);
      expect(box(await mount(ctx(), UNATTRIBUTED))!.disabled).toBe(true);
    });

    it('narrator-only mode lets an unattributed line be selected', async () => {
      expect(box(await mount(ctx({ narratorOnlyMode: true }), UNATTRIBUTED))!.disabled).toBe(false);
    });

    it('there is no checkbox outside the item selection, and none on a pause', async () => {
      expect(
        box(await mount(ctx({ mode: 'speakers', itemSelectable: false }), NARRATION)),
      ).toBeNull();
      expect(box(await mount(ctx(), item('z', { itemType: 'Pause', isPause: true })))).toBeNull();
    });

    it('ticking toggles the store with the chapter ancestry; a selected row is tinted', async () => {
      const el = await mount(ctx(), NARRATION);
      const input = box(el)!;
      input.checked = true;
      input.dispatchEvent(new Event('change'));
      expect(selection.selection()).toEqual({
        n: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' },
      });

      const selected = await mount(ctx({ selectedItems: new Set(['n']) }), NARRATION);
      expect(box(selected)!.checked).toBe(true);
      expect(selected.classList.contains('r2m-item--selected')).toBe(true);
    });
  });

  describe('speaker menu (ticket 12)', () => {
    const speakers = (overrides: Partial<RowContext> = {}) =>
      ctx({ mode: 'speakers', itemSelectable: false, roster: ROSTER, ...overrides });

    it('the chip opens the roster and a pick assigns this item', async () => {
      const el = await mount(speakers(), UNATTRIBUTED);
      const rows = await openChipMenu(el);
      expect(rows.map((r) => text(r.querySelector('.r2m-speaker-menu__name')))).toEqual([
        'Narrator',
        'Hardin',
      ]);
      rows[1]!.click();
      expect(assigner.assign).toHaveBeenCalledWith(
        { kind: 'item', itemId: 'u', paragraphId: 'p1' },
        'h',
      );
    });

    it('Clear speaker and New character go through the assigner too', async () => {
      const el = await mount(speakers(), DIALOG);
      await openChipMenu(el);
      const actions = el.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
      actions[0]!.click();
      expect(assigner.assign).toHaveBeenCalledWith(
        { kind: 'item', itemId: 'd', paragraphId: 'p1' },
        null,
      );
      actions[1]!.click();
      expect(assigner.createAndAssign).toHaveBeenCalledWith(
        { kind: 'item', itemId: 'd', paragraphId: 'p1' },
        '',
      );
    });

    it('the chip is inert in Audio mode, while the paragraph or item is queued, and without a roster', async () => {
      const button = (el: Element) => el.querySelector('r2m-speaker-chip button');
      expect(button(await mount(ctx({ roster: ROSTER }), DIALOG))).toBeNull();
      expect(button(await mount(speakers(), DIALOG))).not.toBeNull();
      expect(button(await mount(speakers({ roster: [] }), DIALOG))).toBeNull();
      const host = document.createElement('div');
      provide(host, SpeakerAssigner, assigner as unknown as SpeakerAssigner);
      document.body.append(host);
      render(
        itemRow(
          DIALOG,
          { paragraphId: 'p1', chapterId: 'c1' },
          speakers(),
          { isFirst: true, isLast: true },
          true,
        ),
        host,
      );
      await Promise.resolve();
      expect(button(host)).toBeNull();
    });
  });
});

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
