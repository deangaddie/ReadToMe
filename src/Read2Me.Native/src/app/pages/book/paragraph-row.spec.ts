import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { ParagraphDto, ParagraphItemDto } from '@app/api';
import { paragraphRow } from './paragraph-row';
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
    roster: ROSTER,
    ...overrides,
  };
}

/** Renders the partial into a host and settles the speaker chips' first render. */
async function mount(context: RowContext, paragraph = PARAGRAPH) {
  const host = document.createElement('div');
  document.body.append(host);
  render(paragraphRow(paragraph, 'c1', context, { isFirst: true, isLast: true }), host);
  await Promise.resolve();
  await Promise.resolve();
  return host.firstElementChild as HTMLElement;
}

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

afterEach(() => document.body.replaceChildren());

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

    it('reflects the selection from the context and highlights the row', async () => {
      const el = await mount(ctx({ selected: new Set(['p1']) }));
      expect(el.classList.contains('r2m-paragraph--selected')).toBe(true);
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

    it('does not show the paragraph attribution chip or lock', async () => {
      const el = await mount({ ...audio, paragraphStatus: { p1: { status: 'Queued' } } });
      expect(el.querySelector('.r2m-paragraph__status .r2m-status-chip')).toBeNull();
      expect(el.querySelector('.r2m-paragraph__status .r2m-paragraph__lock')).toBeNull();
    });

    it('chips are not interactive until the speaker menu lands (native-web 25)', async () => {
      expect((await mount(audio)).querySelectorAll('r2m-speaker-chip button').length).toBe(0);
      expect((await mount(ctx())).querySelectorAll('r2m-speaker-chip button').length).toBe(0);
    });
  });
});
