import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ParagraphDto, ParagraphItemDto } from '@app/api';
import { BookEditor } from './book-editor';
import { ParagraphRow } from './paragraph-row';
import { RowContext } from './reader-rows';
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

const NARRATION_ONLY: ParagraphDto = {
  id: 'p2',
  isPauseParagraph: false,
  items: [item('n2', { itemType: 'Narration', characterId: NARRATOR_ID, text: 'Rain fell.' })],
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
    ancestry: { c1: { partId: 'pt1', volumeId: 'v1' } },
    roster: ROSTER,
    ...overrides,
  };
}

function render(context: RowContext, paragraph = PARAGRAPH) {
  const fixture = TestBed.createComponent(ParagraphRow);
  fixture.componentRef.setInput('paragraph', paragraph);
  fixture.componentRef.setInput('chapterId', 'c1');
  fixture.componentRef.setInput('ctx', context);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('ParagraphRow', () => {
  let assigner: { assign: ReturnType<typeof vi.fn>; createAndAssign: ReturnType<typeof vi.fn>; clearOutcome: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    assigner = {
      assign: vi.fn().mockResolvedValue(undefined),
      createAndAssign: vi.fn().mockResolvedValue(undefined),
      clearOutcome: vi.fn().mockResolvedValue(undefined),
    };
    TestBed.configureTestingModule({
      imports: [ParagraphRow],
      providers: [
        SelectionStore,
        { provide: BookEditor, useValue: { locked: signal(false), run: vi.fn() } },
        { provide: SpeakerAssigner, useValue: assigner },
      ],
    });
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  describe('read mode', () => {
    it('renders prose with one paragraph speaker chip and no item rows', () => {
      const el = render(ctx());
      expect(text(el.querySelector('.r2m-paragraph__prose'))).toBe(
        'Hardin leaned back. "The Encyclopedia comes first." "Then face Anacreon."',
      );
      const chips = el.querySelectorAll('r2m-speaker-chip');
      expect(chips.length).toBe(1);
      expect(text(chips[0]!)).toBe('Hardin +?');
      expect(el.querySelectorAll('r2m-item').length).toBe(0);
    });

    it('shows the attribution queue chip and a lock while queued', () => {
      const el = render(ctx({ paragraphStatus: { p1: { status: 'Queued' } } }));
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Queued');
      expect(el.querySelector('.r2m-paragraph__lock')).not.toBeNull();
    });

    it('an unfinished attribution outcome reads Unknown', () => {
      const el = render(
        ctx({ paragraphStatus: { p1: { outcome: { kind: 'Unfinished', reason: 'x' } } } }),
      );
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Unknown');
    });
  });

  describe('selection (ticket 12)', () => {
    it('a Character paragraph has a live checkbox that toggles the store with its ancestry', () => {
      const el = render(ctx());
      const box = el.querySelector<HTMLInputElement>('input[type=checkbox]')!;
      expect(box.disabled).toBe(false);
      expect(box.checked).toBe(false);

      box.checked = true;
      box.dispatchEvent(new Event('change'));
      const store = TestBed.inject(SelectionStore);
      expect(store.selection()).toEqual({ p1: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' } });

      box.checked = false;
      box.dispatchEvent(new Event('change'));
      expect(store.count()).toBe(0);
    });

    it('reflects the selection from the context and highlights the row', () => {
      const el = render(ctx({ selected: new Set(['p1']) }));
      expect(el.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked).toBe(true);
      expect(el.classList.contains('r2m-paragraph--selected')).toBe(true);
    });

    it('narration-only paragraphs and Audio mode offer no checkbox', () => {
      expect(render(ctx(), NARRATION_ONLY).querySelector('input[type=checkbox]')).toBeNull();
      expect(
        render(ctx({ mode: 'audio', selectable: false })).querySelector('input[type=checkbox]'),
      ).toBeNull();
    });
  });

  describe('speaker menu (ticket 12)', () => {
    const openMenu = (el: HTMLElement, chip: Element) => {
      chip.querySelector<HTMLButtonElement>('button')!.click();
      return Array.from(document.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row'));
    };

    it('the Read chip opens the roster and a pick assigns the whole paragraph', () => {
      const el = render(ctx());
      const rows = openMenu(el, el.querySelector('r2m-speaker-chip')!);
      expect(rows.map((r) => text(r.querySelector('.r2m-speaker-menu__name')))).toEqual([
        'Narrator',
        'Hardin',
      ]);
      rows[1]!.click();
      expect(assigner.assign).toHaveBeenCalledWith({ kind: 'paragraph', paragraphId: 'p1' }, 'h');
    });

    it('Clear speaker and New character go through the assigner too', () => {
      const el = render(ctx());
      openMenu(el, el.querySelector('r2m-speaker-chip')!);
      const actions = document.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
      actions[0]!.click();
      expect(assigner.assign).toHaveBeenCalledWith({ kind: 'paragraph', paragraphId: 'p1' }, null);
      actions[1]!.click();
      expect(assigner.createAndAssign).toHaveBeenCalledWith(
        { kind: 'paragraph', paragraphId: 'p1' },
        '',
      );
    });

    it('in Speakers mode each item chip assigns that item', () => {
      const el = render(ctx({ mode: 'speakers' }));
      const chips = el.querySelectorAll('r2m-item r2m-speaker-chip');
      const rows = openMenu(el, chips[2]!);
      rows[0]!.click();
      expect(assigner.assign).toHaveBeenCalledWith(
        { kind: 'item', itemId: 'u', paragraphId: 'p1' },
        NARRATOR_ID,
      );
    });

    it('a queued paragraph keeps its chips inert', () => {
      const el = render(ctx({ mode: 'speakers', paragraphStatus: { p1: { status: 'Processing' } } }));
      expect(el.querySelectorAll('r2m-speaker-chip button').length).toBe(0);
      const read = render(ctx({ paragraphStatus: { p1: { status: 'Queued' } } }));
      expect(read.querySelectorAll('r2m-speaker-chip button').length).toBe(0);
    });
  });

  describe('outcomes (ticket 12)', () => {
    it('a Failed chip carries the reason and clears the outcome on click', () => {
      const el = render(
        ctx({ paragraphStatus: { p1: { outcome: { kind: 'Failed', reason: 'LLM timed out' } } } }),
      );
      const button = el.querySelector<HTMLButtonElement>('.r2m-paragraph__outcome')!;
      expect(text(button)).toContain('Failed');
      button.click();
      expect(assigner.clearOutcome).toHaveBeenCalledWith('p1');
    });

    it('a queued paragraph with a stale outcome shows Queued and nothing to clear', () => {
      const el = render(
        ctx({ paragraphStatus: { p1: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
      );
      expect(el.querySelector('.r2m-paragraph__outcome')).toBeNull();
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Queued');
    });
  });

  describe('speakers mode', () => {
    it('renders one item row per item with its own speaker chip, unknown highlighted', () => {
      const el = render(ctx({ mode: 'speakers' }));
      const rows = Array.from(el.querySelectorAll('r2m-item'));
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

    it('a pause item inside a paragraph renders as its label', () => {
      const paragraph = {
        ...PARAGRAPH,
        items: [...PARAGRAPH.items, item('z', { itemType: 'Pause', isPause: true, text: null })],
      };
      const el = render(ctx({ mode: 'speakers' }), paragraph);
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

    it('renders voice lines, instructions, review and queue chips per item', () => {
      const el = render(audio);
      const rows = Array.from(el.querySelectorAll('r2m-item'));
      expect(rows.map((r) => text(r.querySelector('[data-testid=voice-line]')))).toEqual([
        'Voice: Deep',
        'Voice: Hardin Voice',
        'Voice: —',
      ]);
      expect(text(rows[1]!.querySelector('.r2m-item__instructions'))).toBe('dry');
      expect(text(rows[1]!.querySelector('r2m-status-chip'))).toContain('Verify failed');
      expect(text(rows[2]!.querySelector('r2m-status-chip'))).toContain('Processing');
      expect(rows[2]!.querySelector('.r2m-item__lock')).not.toBeNull();
    });

    it('offers a player only for items with audio, busting the cache with audioVersion', () => {
      const el = render(audio);
      const players = el.querySelectorAll('r2m-audio-player');
      expect(players.length).toBe(1);
      expect(players[0]!.querySelector('audio')?.getAttribute('src')).toBe(
        '/workspace/dune/audio/d.wav?v=3',
      );
    });

    it('the hub review replaces the REST one once reported', () => {
      const el = render({
        ...audio,
        itemStatus: { ...audio.itemStatus, d: { audioVersion: 4, review: null } },
      });
      expect(el.querySelectorAll('r2m-item')[1]!.querySelector('r2m-status-chip')).toBeNull();
    });

    it('does not show the paragraph attribution chip, but keeps the paragraph menu (disabled while queued)', () => {
      const el = render({ ...audio, paragraphStatus: { p1: { status: 'Queued' } } });
      expect(el.querySelector('.r2m-paragraph__status r2m-status-chip')).toBeNull();
      expect(el.querySelector('.r2m-paragraph__status .r2m-paragraph__lock')).toBeNull();
      const menu = el.querySelector<HTMLButtonElement>('.r2m-paragraph__menu button');
      expect(menu?.disabled).toBe(true);
    });

    it('item chips are not interactive in Audio mode', () => {
      expect(render(audio).querySelectorAll('r2m-speaker-chip button').length).toBe(0);
    });
  });

  describe('menus (ticket 11)', () => {
    it('every row has a menu: the paragraph in all modes, the items in split modes', () => {
      const read = render(ctx());
      expect(read.querySelectorAll('r2m-node-menu').length).toBe(1);
      const speakers = render(ctx({ mode: 'speakers' }));
      expect(speakers.querySelectorAll('r2m-node-menu').length).toBe(4);
    });

    it('a queued paragraph disables the paragraph menu and every item menu', () => {
      const el = render(ctx({ mode: 'speakers', paragraphStatus: { p1: { status: 'Processing' } } }));
      const triggers = Array.from(el.querySelectorAll<HTMLButtonElement>('r2m-node-menu button'));
      expect(triggers.length).toBe(4);
      expect(triggers.every((b) => b.disabled)).toBe(true);
    });

    it('an audio-queued item disables only its own menu', () => {
      const el = render(
        ctx({ mode: 'audio', selectable: false, itemStatus: { d: { status: 'Queued' } } }),
      );
      const items = Array.from(el.querySelectorAll('r2m-item'));
      const disabled = items.map(
        (i) => i.querySelector<HTMLButtonElement>('r2m-node-menu button')!.disabled,
      );
      expect(disabled).toEqual([false, true, false]);
    });
  });
});
