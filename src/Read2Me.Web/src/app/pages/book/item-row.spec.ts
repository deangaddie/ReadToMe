import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { render } from 'lit-html';
import { ITEM_AUDIO_NOT_KEPT, type ParagraphItemDto, VIEWING_READ_ONLY } from '@app/api';
import { provide } from '@app/core/services';
import {
  type ConfirmOptions,
  ConfirmService,
  PromptService,
  type TextPromptOptions,
} from '@app/ui/dialogs';
import { AudioGenerator } from './audio-generator';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import { menuBookStore } from '../../../testing/structure-rules';
import { itemRow } from './item-row';
import { NODE_MENU_OPEN, type NodeMenuRequest } from './node-menu';
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
    readOnly: null,
    generating: false,
    roster: [],
    ...overrides,
  };
}

/** The shell's page-scoped collaborators, reached by the row through DOM ancestry. */
let selection: AudioSelectionStore;
let assigner: { assign: ReturnType<typeof mock>; createAndAssign: ReturnType<typeof mock> };
let generator: { retry: ReturnType<typeof mock>; dismissReview: ReturnType<typeof mock> };
let editor: { locked: () => boolean; run: ReturnType<typeof mock> };
let prompt: { text: ReturnType<typeof mock>; answer: string | null };
let confirm: { confirm: ReturnType<typeof mock> };

async function mount(context: RowContext, row: ParagraphItemDto) {
  const host = document.createElement('div');
  provide(host, AudioSelectionStore, selection);
  provide(host, SpeakerAssigner, assigner as unknown as SpeakerAssigner);
  provide(host, AudioGenerator, generator as unknown as AudioGenerator);
  provide(host, BookEditor, editor as unknown as BookEditor);
  provide(host, BookStore, menuBookStore());
  provide(host, PromptService, prompt as unknown as PromptService);
  provide(host, ConfirmService, confirm as unknown as ConfirmService);
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
  generator = { retry: mock(async () => true), dismissReview: mock(async () => true) };
  editor = { locked: () => false, run: mock(async () => true) };
  confirm = { confirm: mock(async (_options: ConfirmOptions) => false) };
  prompt = {
    answer: 'shouting',
    text: mock(async (_options: TextPromptOptions) => prompt.answer),
  };
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

  describe('delivery cue (delivery-cue-editing 01)', () => {
    const speakers = () => ctx({ mode: 'speakers', itemSelectable: false });
    const cued = item('c', { characterId: 'h', voiceInstructions: 'whispered' });

    it('a speech item with a cue shows it under its text, styled as in Audio mode', async () => {
      const el = await mount(speakers(), cued);
      const cue = el.querySelector('[data-testid=delivery-cue]')!;
      expect(text(cue)).toBe('whispered');
      expect(cue.classList.contains('r2m-item__instructions')).toBe(true);
      // Under the text, not on the speaker line.
      expect(el.querySelector('.r2m-item__line [data-testid=delivery-cue]')).toBeNull();
      expect(el.querySelector('.r2m-item__line')!.nextElementSibling).toBe(cue.parentElement);
    });

    it('a narration item shows its cue too', async () => {
      const el = await mount(speakers(), { ...NARRATION, voiceInstructions: 'slowly' });
      expect(text(el.querySelector('[data-testid=delivery-cue]'))).toBe('slowly');
    });

    it('an item with no cue renders no cue element at all', async () => {
      const el = await mount(speakers(), DIALOG);
      expect(el.querySelector('[data-testid=delivery-cue]')).toBeNull();
      expect(el.children).toHaveLength(1);
    });

    it('Audio mode keeps its one cue on the audio line', async () => {
      const el = await mount(ctx(), cued);
      const cues = el.querySelectorAll('[data-testid=delivery-cue]');
      expect(cues).toHaveLength(1);
      expect(cues[0]!.closest('.r2m-item__audio')).not.toBeNull();
    });
  });

  describe('editing the delivery cue (delivery-cue-editing 03)', () => {
    const cueCtx = (overrides: Partial<RowContext> = {}) =>
      ctx({ mode: 'speakers', itemSelectable: false, ...overrides });
    const cued = item('c', { characterId: 'h', voiceInstructions: 'whispered' });
    const cueButton = (el: Element) =>
      el.querySelector<HTMLButtonElement>('button[data-testid=delivery-cue]');

    it.each(['speakers', 'audio'] as const)(
      'clicking the cue in %s mode opens the edit dialog and posts the new cue',
      async (mode) => {
        const el = await mount(cueCtx({ mode }), cued);
        const cue = cueButton(el)!;
        expect(cue.getAttribute('aria-label')).toBe('Edit delivery cue: whispered');
        cue.click();
        await Bun.sleep(0);
        expect(prompt.text.mock.calls[0]![0]).toMatchObject({
          title: 'Edit delivery cue',
          initial: 'whispered',
        });
        expect(editor.run.mock.calls).toEqual([
          [{ type: 'SetItemDeliveryCue', itemId: 'c', cue: 'shouting' }],
        ]);
      },
    );

    it('a cancelled dialog sends nothing', async () => {
      prompt.answer = null;
      const el = await mount(cueCtx(), cued);
      cueButton(el)!.click();
      await Bun.sleep(0);
      expect(prompt.text).toHaveBeenCalledTimes(1);
      expect(editor.run).not.toHaveBeenCalled();
    });

    it('the cue is not clickable while the editor is locked', async () => {
      const el = await mount(cueCtx({ locked: true }), cued);
      expect(cueButton(el)!.disabled).toBe(true);
    });

    it('the item menu knows the cue, so it offers Edit/Remove rather than Add', async () => {
      const opened: NodeMenuRequest[] = [];
      const el = await mount(cueCtx(), cued);
      el.parentElement!.addEventListener(NODE_MENU_OPEN, (e) =>
        opened.push((e as CustomEvent<NodeMenuRequest>).detail),
      );
      el.querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!.click();
      expect(opened[0]!.target).toMatchObject({
        kind: 'item',
        id: 'c',
        cue: 'whispered',
        hasAudio: false,
      });
    });

    it('clicking the cue of an item with audio asks before discarding it (delivery-cue-editing 04)', async () => {
      const el = await mount(cueCtx(), { ...cued, audioFileName: 'audio/c.wav' });
      cueButton(el)!.click();
      await Bun.sleep(0);
      expect(confirm.confirm).toHaveBeenCalledTimes(1);
      expect(editor.run).not.toHaveBeenCalled();

      confirm.confirm.mockImplementation(async () => true);
      cueButton(el)!.click();
      await Bun.sleep(0);
      expect(editor.run.mock.calls).toEqual([
        [{ type: 'SetItemDeliveryCue', itemId: 'c', cue: 'shouting' }],
      ]);
    });
  });
});

describe('itemRow (audio)', () => {
  describe('retry flow (ticket 13)', () => {
    it('a settled failure shows its reason and a Retry that re-queues just this item', async () => {
      const el = await mount(
        ctx({ itemStatus: { n: { outcome: { kind: 'Failed', reason: 'No default voice' } } } }),
        NARRATION,
      );
      expect(text(el.querySelector('.r2m-status-chip'))).toContain('Failed');
      const retry = el.querySelector<HTMLButtonElement>('[data-action=retry-audio]')!;
      expect(retry.disabled).toBe(false);
      retry.click();
      expect(generator.retry).toHaveBeenCalledWith('n');
    });

    it('an unfinished take can be retried too; a queued or clean item offers no Retry', async () => {
      const unfinished = await mount(
        ctx({ itemStatus: { n: { outcome: { kind: 'Unfinished', reason: 'cancelled' } } } }),
        NARRATION,
      );
      expect(text(unfinished.querySelector('.r2m-status-chip'))).toContain('Unfinished');
      expect(unfinished.querySelector('[data-action=retry-audio]')).not.toBeNull();

      const queued = await mount(
        ctx({ itemStatus: { n: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
        NARRATION,
      );
      expect(queued.querySelector('[data-action=retry-audio]')).toBeNull();
      expect((await mount(ctx(), NARRATION)).querySelector('[data-action=retry-audio]')).toBeNull();
    });

    it('a line nobody can read yet has no Retry; a generating or locked page disables it', async () => {
      const failed = { outcome: { kind: 'Failed' as const } };
      const unvoiced = await mount(ctx({ itemStatus: { u: failed } }), UNATTRIBUTED);
      expect(text(unvoiced.querySelector('.r2m-status-chip'))).toContain('Failed');
      expect(unvoiced.querySelector('[data-action=retry-audio]')).toBeNull();
      const busy = await mount(ctx({ itemStatus: { n: failed }, generating: true }), NARRATION);
      expect(busy.querySelector<HTMLButtonElement>('[data-action=retry-audio]')!.disabled).toBe(
        true,
      );
      const locked = await mount(ctx({ itemStatus: { n: failed }, locked: true }), NARRATION);
      expect(locked.querySelector<HTMLButtonElement>('[data-action=retry-audio]')!.disabled).toBe(
        true,
      );
    });
  });

  it('offers a player only for items with audio, busting the cache with audioVersion', async () => {
    const el = await mount(ctx({ itemStatus: { d: { audioVersion: 3 } } }), DIALOG);
    const player = el.querySelector('r2m-audio-player')!;
    await player.rendered();
    expect(player.querySelector('audio')?.getAttribute('src')).toBe(
      '/workspace/dune/audio/d.wav?v=3',
    );
    expect((await mount(ctx(), NARRATION)).querySelector('r2m-audio-player')).toBeNull();
    expect(
      (await mount(ctx({ mode: 'speakers', itemSelectable: false }), DIALOG)).querySelector(
        'r2m-audio-player',
      ),
    ).toBeNull();
  });

  describe('viewing a History step (ticket 32)', () => {
    const viewing = (overrides: Partial<RowContext> = {}) =>
      ctx({ locked: true, readOnly: VIEWING_READ_ONLY, ...overrides });
    const tooltip = (el: Element | null) =>
      el?.closest('.r2m-blockable')?.getAttribute('data-tooltip') ?? null;

    it('item play is off and says item audio is not kept, never playing the current take', async () => {
      const el = await mount(viewing({ itemStatus: { d: { audioVersion: 3 } } }), DIALOG);
      const player = el.querySelector('r2m-audio-player')!;
      await player.rendered();
      expect(player.querySelector('audio')?.getAttribute('src') ?? null).toBeNull();
      expect(player.querySelector<HTMLButtonElement>('button')!.disabled).toBe(true);
      expect(tooltip(player)).toBe(ITEM_AUDIO_NOT_KEPT);
    });

    it('the node menu, Retry and Dismiss are off with the viewing reason', async () => {
      const el = await mount(
        viewing({
          itemStatus: { d: { outcome: { kind: 'Failed' } } },
          reviews: {
            d: {
              state: 'NeedsReview',
              normalizeOk: true,
              normalizeReason: null,
              verifyOk: false,
              wer: 0.3,
              verifyReason: 'transcript drifted',
              transcript: null,
              originalTextSnapshot: null,
            },
          },
        }),
        DIALOG,
      );
      for (const selector of [
        '.r2m-node-menu__trigger',
        '[data-action=retry-audio]',
        '[data-action=dismiss-review]',
      ]) {
        const control = el.querySelector<HTMLButtonElement>(selector)!;
        expect(control.disabled).toBe(true);
        expect(tooltip(control)).toBe(VIEWING_READ_ONLY);
      }
    });

    it('the speaker chip offers no menu', async () => {
      const el = await mount(
        viewing({ mode: 'speakers', itemSelectable: false, roster: ROSTER }),
        DIALOG,
      );
      expect(el.querySelector('r2m-speaker-chip button')).toBeNull();
    });
  });

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

    it('a verify failure carries a Dismiss that posts through the generator; a dismissed review has none', async () => {
      const el = await mount(ctx({ reviews: { d: review } }), DIALOG);
      el.querySelector<HTMLButtonElement>('[data-action=dismiss-review]')!.click();
      expect(generator.dismissReview).toHaveBeenCalledWith('d');

      const dismissed = await mount(
        ctx({ reviews: { d: { ...review, state: 'Dismissed' } } }),
        DIALOG,
      );
      expect(dismissed.querySelector('[data-action=dismiss-review]')).toBeNull();

      const locked = await mount(ctx({ reviews: { d: review }, locked: true }), DIALOG);
      expect(
        locked.querySelector<HTMLButtonElement>('[data-action=dismiss-review]')!.disabled,
      ).toBe(true);
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
