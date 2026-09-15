import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ParagraphItemDto } from '@app/api';
import { AudioGenerator } from './audio-generator';
import { AudioSelectionStore } from './audio-selection-store';
import { BookEditor } from './book-editor';
import { ItemRow } from './item-row';
import { RowContext } from './reader-rows';
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

function render(context: RowContext, row: ParagraphItemDto) {
  const fixture = TestBed.createComponent(ItemRow);
  fixture.componentRef.setInput('item', row);
  fixture.componentRef.setInput('paragraphId', 'p1');
  fixture.componentRef.setInput('chapterId', 'c1');
  fixture.componentRef.setInput('ctx', context);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
const box = (el: HTMLElement) => el.querySelector<HTMLInputElement>('input[type=checkbox]');

describe('ItemRow (audio, ticket 13)', () => {
  let generator: {
    retry: ReturnType<typeof vi.fn>;
    dismissReview: ReturnType<typeof vi.fn>;
    working: ReturnType<typeof signal<boolean>>;
  };

  beforeEach(() => {
    generator = {
      retry: vi.fn().mockResolvedValue(true),
      dismissReview: vi.fn().mockResolvedValue(true),
      working: signal(false),
    };
    TestBed.configureTestingModule({
      imports: [ItemRow],
      providers: [
        AudioSelectionStore,
        { provide: AudioGenerator, useValue: generator },
        { provide: BookEditor, useValue: { locked: signal(false), run: vi.fn() } },
        { provide: SpeakerAssigner, useValue: { assign: vi.fn(), createAndAssign: vi.fn() } },
      ],
    });
  });

  describe('checkbox enablement', () => {
    it('narration and attributed lines can be selected; an unattributed line cannot', () => {
      expect(box(render(ctx(), NARRATION))!.disabled).toBe(false);
      expect(box(render(ctx(), DIALOG))!.disabled).toBe(false);
      expect(box(render(ctx(), UNATTRIBUTED))!.disabled).toBe(true);
    });

    it('narrator-only mode lets an unattributed line be selected', () => {
      expect(box(render(ctx({ narratorOnlyMode: true }), UNATTRIBUTED))!.disabled).toBe(false);
    });

    it('there is no checkbox outside the item selection, and none on a pause', () => {
      expect(box(render(ctx({ mode: 'speakers', itemSelectable: false }), NARRATION))).toBeNull();
      expect(box(render(ctx(), item('z', { itemType: 'Pause', isPause: true })))).toBeNull();
    });

    it('ticking toggles the store with the chapter ancestry; a selected row is tinted', () => {
      const el = render(ctx(), NARRATION);
      const input = box(el)!;
      input.checked = true;
      input.dispatchEvent(new Event('change'));
      const store = TestBed.inject(AudioSelectionStore);
      expect(store.selection()).toEqual({ n: { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' } });

      const selected = render(ctx({ selectedItems: new Set(['n']) }), NARRATION);
      expect(box(selected)!.checked).toBe(true);
      expect(selected.classList.contains('r2m-item--selected')).toBe(true);
    });
  });

  describe('retry flow', () => {
    it('a settled failure shows its reason and a Retry that re-queues just this item', () => {
      const el = render(
        ctx({ itemStatus: { n: { outcome: { kind: 'Failed', reason: 'No default voice' } } } }),
        NARRATION,
      );
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Failed');
      expect(el.querySelector('r2m-status-chip')?.getAttribute('title')).toBeNull();
      const retry = el.querySelector<HTMLButtonElement>('[data-action=retry-audio]')!;
      expect(retry.disabled).toBe(false);
      retry.click();
      expect(generator.retry).toHaveBeenCalledWith('n');
    });

    it('an unfinished take can be retried too; a queued or clean item offers no Retry', () => {
      const unfinished = render(
        ctx({ itemStatus: { n: { outcome: { kind: 'Unfinished', reason: 'cancelled' } } } }),
        NARRATION,
      );
      expect(text(unfinished.querySelector('r2m-status-chip'))).toContain('Unfinished');
      expect(unfinished.querySelector('[data-action=retry-audio]')).not.toBeNull();

      const queued = render(
        ctx({ itemStatus: { n: { status: 'Queued', outcome: { kind: 'Failed' } } } }),
        NARRATION,
      );
      expect(text(queued.querySelector('r2m-status-chip'))).toContain('Queued');
      expect(queued.querySelector('[data-action=retry-audio]')).toBeNull();
      expect(queued.querySelector('.r2m-item__lock')).not.toBeNull();

      expect(render(ctx(), NARRATION).querySelector('[data-action=retry-audio]')).toBeNull();
    });

    it('a failed line nobody can read yet offers no Retry', () => {
      const el = render(
        ctx({ itemStatus: { u: { outcome: { kind: 'Failed', reason: 'No character assigned' } } } }),
        UNATTRIBUTED,
      );
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Failed');
      expect(el.querySelector('[data-action=retry-audio]')).toBeNull();
    });

    it('Retry waits while another request is in flight', () => {
      generator.working.set(true);
      const el = render(
        ctx({ itemStatus: { n: { outcome: { kind: 'Failed' } } } }),
        NARRATION,
      );
      expect(el.querySelector<HTMLButtonElement>('[data-action=retry-audio]')!.disabled).toBe(true);
    });
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

    it('a verify failure is a warn chip with a Dismiss that posts the command', () => {
      const el = render(ctx({ reviews: { d: review } }), DIALOG);
      const chip = el.querySelector('r2m-status-chip')!;
      expect(text(chip)).toContain('Verify failed');
      expect(chip.classList.contains('r2m-status-chip--warn')).toBe(true);
      el.querySelector<HTMLButtonElement>('[data-action=dismiss-review]')!.click();
      expect(generator.dismissReview).toHaveBeenCalledWith('d');
    });

    it('a normalize failure is an error chip', () => {
      const el = render(
        ctx({ reviews: { d: { ...review, normalizeOk: false, normalizeReason: 'clipped' } } }),
        DIALOG,
      );
      expect(text(el.querySelector('r2m-status-chip'))).toContain('Normalize failed');
      expect(el.querySelector('r2m-status-chip')!.classList.contains('r2m-status-chip--error')).toBe(true);
    });

    it('a dismissed review is a faded icon with no chip and no Dismiss', () => {
      const el = render(ctx({ reviews: { d: { ...review, state: 'Dismissed' } } }), DIALOG);
      expect(el.querySelector('r2m-status-chip')).toBeNull();
      expect(el.querySelector('[data-action=dismiss-review]')).toBeNull();
      expect(el.querySelector('[data-testid=review-dismissed]')).not.toBeNull();
    });

    it('the hub review wins over the REST map: a null hub review clears the chip', () => {
      const el = render(
        ctx({ reviews: { d: review }, itemStatus: { d: { audioVersion: 2, review: null } } }),
        DIALOG,
      );
      expect(el.querySelector('r2m-status-chip')).toBeNull();
      expect(el.querySelector('audio')?.getAttribute('src')).toBe('/workspace/dune/audio/d.wav?v=2');
    });
  });
});
