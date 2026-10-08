import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { render } from 'lit-html';
import type { ParagraphDto, ParagraphItemDto } from '@app/api';
import { provide } from '@app/core/services';
import { paragraphRow } from './paragraph-row';
import type { RowContext } from './reader-rows';
import { SelectionStore } from './selection-store';
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

const PARAGRAPH: ParagraphDto = {
  id: 'p1',
  isPauseParagraph: false,
  items: [
    item('n', { itemType: 'Narration', characterId: NARRATOR_ID, text: 'Hardin leaned back.' }),
    item('d', {
      characterId: 'h',
      text: '"The Encyclopedia comes first."',
      audioFileName: 'audio/d.wav',
      voiceInstructions: 'dry',
    }),
    item('u', { text: '"Then face Anacreon."' }),
  ],
};

const ROSTER = [
  { id: NARRATOR_ID, name: 'Narrator', isNarrator: true },
  { id: 'h', name: 'Hardin' },
];

function ctx(overrides: Partial<RowContext> = {}): RowContext {
  return {
    folder: 'dune',
    mode: 'read',
    speakers: {
      names: { h: 'Hardin' },
      narrator: { characterId: NARRATOR_ID, displayName: 'Narrator', isLinked: false },
    },
    paragraphStatus: {},
    itemStatus: {},
    voices: {},
    reviews: {},
    selectable: true,
    selected: new Set(),
    itemSelectable: false,
    selectedItems: new Set(),
    narratorOnlyMode: false,
    ancestry: { c1: { partId: 'pt1', volumeId: 'v1' } },
    locked: false,
    roster: ROSTER,
    ...overrides,
  };
}

const NARRATION_ONLY: ParagraphDto = {
  id: 'p2',
  isPauseParagraph: false,
  items: [item('n2', { itemType: 'Narration', characterId: NARRATOR_ID, text: 'Dawn broke.' })],
};

/** The shell's page-scoped collaborators, reached by the row through DOM ancestry. */
let selection: SelectionStore;
let assigner: {
  assign: ReturnType<typeof mock>;
  createAndAssign: ReturnType<typeof mock>;
  clearOutcome: ReturnType<typeof mock>;
};

/** Renders the partial into a host and settles the speaker chips' first render. */
async function mount(context: RowContext, paragraph = PARAGRAPH) {
  const host = document.createElement('div');
  provide(host, SelectionStore, selection);
  provide(host, SpeakerAssigner, assigner as unknown as SpeakerAssigner);
  document.body.append(host);
  render(paragraphRow(paragraph, 'c1', context, { isFirst: true, isLast: true }), host);
  await Promise.resolve();
  await Promise.resolve();
  return host.firstElementChild as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

// happy-dom has no popover: the chip's menu opens on the `toggle` event the browser would fire.
const proto = HTMLElement.prototype as unknown as Record<string, () => void>;
beforeEach(() => {
  selection = new SelectionStore();
  assigner = {
    assign: mock(async () => undefined),
    createAndAssign: mock(async () => undefined),
    clearOutcome: mock(async () => undefined),
  };
  proto['hidePopover'] = () => undefined;
});
afterEach(() => {
  delete proto['hidePopover'];
  document.body.replaceChildren();
});

async function openChipMenu(chip: Element) {
  chip.querySelector<HTMLButtonElement>('button')!.click();
  chip
    .querySelector('.r2m-speaker-chip__menu')!
    .dispatchEvent(Object.assign(new Event('toggle'), { newState: 'open' }));
  await (chip as HTMLElement & { rendered(): Promise<void> }).rendered();
  await chip.querySelector('r2m-speaker-menu')!.rendered();
  return Array.from(chip.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row'));
}

describe('paragraphRow', () => {
  describe('read mode', () => {
    it('renders prose with one paragraph speaker chip and no item rows', async () => {
      const el = await mount(ctx());
      expect(el.classList.contains('r2m-paragraph')).toBe(true);
      expect(el.dataset['paragraphId']).toBe('p1');
      expect(text(el.querySelector('.r2m-paragraph__prose'))).toBe(
        'Hardin leaned back. "The Encyclopedia comes first." "Then face Anacreon."',
      );
      const chips = el.querySelectorAll('r2m-speaker-chip');
      expect(chips.length).toBe(1);
      expect(text(chips[0]!)).toBe('Hardin +?');
      expect(el.querySelectorAll('.r2m-item').length).toBe(0);
    });

    it('shows the attribution queue chip and a lock while queued', async () => {
      const el = await mount(ctx({ paragraphStatus: { p1: { status: 'Queued' } } }));
      expect(text(el.querySelector('.r2m-status-chip'))).toContain('Queued');
      expect(el.querySelector('.r2m-paragraph__lock')).not.toBeNull();
    });

    it('an unfinished attribution outcome reads Unknown', async () => {
      const el = await mount(
        ctx({ paragraphStatus: { p1: { outcome: { kind: 'Unfinished', reason: 'x' } } } }),
      );
      expect(text(el.querySelector('.r2m-status-chip'))).toContain('Unknown');
      expect(el.querySelector('.r2m-paragraph__lock')).toBeNull();
    });

    it('a queued paragraph with a stale outcome shows Queued', async () => {
      const el = await mount(
        ctx({ paragraphStatus: { p1: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
      );
      expect(text(el.querySelector('.r2m-status-chip'))).toContain('Queued');
    });

    it('colours the row by the paragraph speaker and marks its state', async () => {
      const el = await mount(ctx());
      expect(el.classList.contains('r2m-paragraph--named')).toBe(true);
      expect(el.style.getPropertyValue('--r2m-speaker-hue')).not.toBe('');
      const unknown = await mount(ctx(), { ...PARAGRAPH, items: [item('u')] });
      expect(unknown.classList.contains('r2m-paragraph--unknown')).toBe(true);
    });
  });

  describe('selection (ticket 12)', () => {
    it('a Character paragraph has a live checkbox that toggles the store with its ancestry', async () => {
      const el = await mount(ctx());
      const box = el.querySelector<HTMLInputElement>('input[type=checkbox]')!;
      expect(box.disabled).toBe(false);
      expect(box.checked).toBe(false);

      box.checked = true;
      box.dispatchEvent(new Event('change'));
      expect(selection.selection()).toEqual({
        p1: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' },
      });

      box.checked = false;
      box.dispatchEvent(new Event('change'));
      expect(selection.count()).toBe(0);
    });

    it('reflects the selection from the context and highlights the row', async () => {
      const el = await mount(ctx({ selected: new Set(['p1']) }));
      expect(el.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked).toBe(true);
      expect(el.classList.contains('r2m-paragraph--selected')).toBe(true);
    });

    it('narration-only paragraphs and Audio mode offer no checkbox', async () => {
      expect((await mount(ctx(), NARRATION_ONLY)).querySelector('input[type=checkbox]')).toBeNull();
      expect(
        (await mount(ctx({ mode: 'audio', selectable: false }))).querySelector(
          '.r2m-paragraph__gutter input[type=checkbox]',
        ),
      ).toBeNull();
    });
  });

  describe('speaker menu (ticket 12)', () => {
    it('the Read chip opens the roster and a pick assigns the whole paragraph', async () => {
      const el = await mount(ctx());
      const rows = await openChipMenu(el.querySelector('r2m-speaker-chip')!);
      expect(rows.map((r) => text(r.querySelector('.r2m-speaker-menu__name')))).toEqual([
        'Narrator',
        'Hardin',
      ]);
      rows[1]!.click();
      expect(assigner.assign).toHaveBeenCalledWith({ kind: 'paragraph', paragraphId: 'p1' }, 'h');
    });

    it('Clear speaker and New character go through the assigner too', async () => {
      const el = await mount(ctx());
      await openChipMenu(el.querySelector('r2m-speaker-chip')!);
      const actions = el.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
      actions[0]!.click();
      expect(assigner.assign).toHaveBeenCalledWith({ kind: 'paragraph', paragraphId: 'p1' }, null);
      actions[1]!.click();
      expect(assigner.createAndAssign).toHaveBeenCalledWith(
        { kind: 'paragraph', paragraphId: 'p1' },
        '',
      );
    });

    it('in Speakers mode each item chip assigns that item', async () => {
      const el = await mount(ctx({ mode: 'speakers' }));
      const chips = el.querySelectorAll('.r2m-item r2m-speaker-chip');
      const rows = await openChipMenu(chips[2]!);
      rows[0]!.click();
      expect(assigner.assign).toHaveBeenCalledWith(
        { kind: 'item', itemId: 'u', paragraphId: 'p1' },
        NARRATOR_ID,
      );
    });

    it('a queued paragraph keeps its chips inert', async () => {
      const el = await mount(
        ctx({ mode: 'speakers', paragraphStatus: { p1: { status: 'Processing' } } }),
      );
      expect(el.querySelectorAll('r2m-speaker-chip button').length).toBe(0);
      const read = await mount(ctx({ paragraphStatus: { p1: { status: 'Queued' } } }));
      expect(read.querySelectorAll('r2m-speaker-chip button').length).toBe(0);
    });
  });

  describe('outcomes (ticket 12)', () => {
    it('a Failed chip carries the reason and clears the outcome on click', async () => {
      const el = await mount(
        ctx({ paragraphStatus: { p1: { outcome: { kind: 'Failed', reason: 'LLM timed out' } } } }),
      );
      const button = el.querySelector<HTMLButtonElement>('.r2m-paragraph__outcome')!;
      expect(text(button)).toContain('Failed');
      expect(button.getAttribute('data-tooltip')).toBe('LLM timed out — Click to clear');
      button.click();
      expect(assigner.clearOutcome).toHaveBeenCalledWith('p1');
    });

    it('a queued paragraph with a stale outcome shows Queued and nothing to clear', async () => {
      const el = await mount(
        ctx({ paragraphStatus: { p1: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
      );
      expect(el.querySelector('.r2m-paragraph__outcome')).toBeNull();
      expect(text(el.querySelector('.r2m-status-chip'))).toContain('Queued');
    });
  });

  describe('speakers mode', () => {
    it('renders one item row per item with its own speaker chip, unknown highlighted', async () => {
      const el = await mount(ctx({ mode: 'speakers' }));
      const rows = Array.from(el.querySelectorAll('.r2m-item'));
      expect(rows.map((r) => text(r.querySelector('.r2m-speaker-chip__name')))).toEqual([
        'Narration',
        'Hardin',
        'Unknown',
      ]);
      expect(rows.map((r) => r.classList.contains('r2m-item--unknown'))).toEqual([
        false,
        false,
        true,
      ]);
      expect(el.querySelector('.r2m-paragraph__prose')).toBeNull();
      expect(el.querySelector('[data-testid=voice-line]')).toBeNull();
    });

    it('a pause item inside a paragraph renders as its label', async () => {
      const paragraph = {
        ...PARAGRAPH,
        items: [...PARAGRAPH.items, item('z', { itemType: 'Pause', isPause: true, text: null })],
      };
      const el = await mount(ctx({ mode: 'speakers' }), paragraph);
      expect(text(el.querySelector('.r2m-item--pause'))).toBe('Pause');
    });
  });

  describe('audio mode', () => {
    const audio = ctx({
      mode: 'audio',
      selectable: false,
      voices: {
        n: { voiceName: 'Deep', narratedBy: null },
        d: { voiceName: 'Hardin Voice', narratedBy: null },
        u: { voiceName: null, narratedBy: null },
      },
      itemStatus: { d: { audioVersion: 3 }, u: { status: 'Processing' } },
      reviews: {
        d: {
          state: 'NeedsReview',
          normalizeOk: true,
          normalizeReason: null,
          verifyOk: false,
          wer: 0.3,
          verifyReason: null,
          transcript: null,
          originalTextSnapshot: null,
        },
      },
    });

    it('renders voice lines, instructions, review and queue chips per item', async () => {
      const el = await mount(audio);
      const rows = Array.from(el.querySelectorAll('.r2m-item'));
      expect(rows.map((r) => text(r.querySelector('[data-testid=voice-line]')))).toEqual([
        'Voice: Deep',
        'Voice: Hardin Voice',
        'Voice: —',
      ]);
      expect(text(rows[1]!.querySelector('.r2m-item__instructions'))).toBe('dry');
      expect(text(rows[1]!.querySelector('.r2m-status-chip'))).toContain('Verify failed');
      expect(text(rows[2]!.querySelector('.r2m-status-chip'))).toContain('Processing');
      expect(rows[2]!.querySelector('.r2m-item__lock')).not.toBeNull();
    });

    it('the hub review replaces the REST one once reported', async () => {
      const el = await mount({
        ...audio,
        itemStatus: { ...audio.itemStatus, d: { audioVersion: 4, review: null } },
      });
      expect(el.querySelectorAll('.r2m-item')[1]!.querySelector('.r2m-status-chip')).toBeNull();
    });

    it('does not show the paragraph attribution chip or lock, but keeps the paragraph menu (disabled while queued)', async () => {
      const el = await mount({ ...audio, paragraphStatus: { p1: { status: 'Queued' } } });
      expect(el.querySelector('.r2m-paragraph__status .r2m-status-chip')).toBeNull();
      expect(el.querySelector('.r2m-paragraph__status .r2m-paragraph__lock')).toBeNull();
      const menu = el.querySelector<HTMLButtonElement>('.r2m-paragraph__menu button');
      expect(menu?.disabled).toBe(true);
    });

    it('item chips are inert in Audio mode: the item selection checkbox is what the row offers', async () => {
      const el = await mount({ ...audio, itemSelectable: true });
      expect(el.querySelectorAll('r2m-speaker-chip button').length).toBe(0);
      expect(el.querySelectorAll('.r2m-item input[type=checkbox]').length).toBe(3);
    });
  });

  describe('menus (ticket 11)', () => {
    const triggers = (el: Element) =>
      Array.from(el.querySelectorAll<HTMLButtonElement>('.r2m-node-menu__trigger'));

    it('every row has a menu: the paragraph in all modes, the items in split modes', async () => {
      const read = await mount(ctx());
      expect(triggers(read).length).toBe(1);
      expect(triggers(read)[0]!.getAttribute('aria-label')).toBe(
        'Actions for Hardin leaned back. "The Encyclopedia comes first." "Then face Anacreon."',
      );
      const speakers = await mount(ctx({ mode: 'speakers' }));
      expect(triggers(speakers).length).toBe(4);
      expect(speakers.querySelectorAll('.r2m-item__menu .r2m-node-menu__trigger').length).toBe(3);
    });

    it('a queued paragraph disables the paragraph menu and every item menu', async () => {
      const el = await mount(
        ctx({ mode: 'speakers', paragraphStatus: { p1: { status: 'Processing' } } }),
      );
      expect(triggers(el).length).toBe(4);
      expect(triggers(el).every((b) => b.disabled)).toBe(true);
    });

    it('an audio-queued item disables only its own menu', async () => {
      const el = await mount(
        ctx({ mode: 'audio', selectable: false, itemStatus: { d: { status: 'Queued' } } }),
      );
      const items = Array.from(el.querySelectorAll('.r2m-item'));
      expect(
        items.map((i) => i.querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!.disabled),
      ).toEqual([false, true, false]);
    });

    it('a locked editor disables every menu', async () => {
      const el = await mount(ctx({ mode: 'speakers', locked: true }));
      expect(triggers(el).every((b) => b.disabled)).toBe(true);
    });
  });
});
