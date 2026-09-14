import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ParagraphDto, ParagraphItemDto } from '@app/api';
import { BookEditor } from './book-editor';
import { ParagraphRow } from './paragraph-row';
import { RowContext } from './reader-rows';

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
    ...overrides,
  };
}

function render(context: RowContext, paragraph = PARAGRAPH) {
  const fixture = TestBed.createComponent(ParagraphRow);
  fixture.componentRef.setInput('paragraph', paragraph);
  fixture.componentRef.setInput('ctx', context);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('ParagraphRow', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [ParagraphRow],
      providers: [{ provide: BookEditor, useValue: { locked: signal(false), run: vi.fn() } }],
    }),
  );

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

    it('the gutter checkbox is present but disabled', () => {
      const box = render(ctx()).querySelector<HTMLInputElement>('input[type=checkbox]');
      expect(box?.disabled).toBe(true);
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
      const el = render(ctx({ mode: 'audio', itemStatus: { d: { status: 'Queued' } } }));
      const items = Array.from(el.querySelectorAll('r2m-item'));
      const disabled = items.map(
        (i) => i.querySelector<HTMLButtonElement>('r2m-node-menu button')!.disabled,
      );
      expect(disabled).toEqual([false, true, false]);
    });
  });
});
