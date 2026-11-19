import { describe, expect, it } from 'bun:test';
import { ITEM_AUDIO_NOT_KEPT, STALE_READ_ONLY, VIEWING_READ_ONLY } from '@app/api';
import { type RowContextInputs, rowContext } from './row-context';

const LIT: ReadonlySet<string> = new Set(['pron-1']);

/** A loaded book in Read mode: nothing viewed, nothing blocked, the Pronunciations panel shut. */
const INPUTS: RowContextInputs = {
  folder: 'dracula',
  mode: 'read',
  speakers: { names: { h: 'Harker' }, narrator: null },
  paragraphStatus: {},
  itemStatus: {},
  voices: {},
  reviews: {},
  kind: 'paragraphs',
  selected: new Set(['p1']),
  selectedItems: new Set(['p1-i']),
  narratorOnlyMode: false,
  ancestry: {},
  roster: [],
  block: null,
  viewing: false,
  generating: false,
  marks: {},
  pronunciationsOpen: false,
  lit: LIT,
};

describe('rowContext (what every reader row reads)', () => {
  it('passes the plain values through', () => {
    const ctx = rowContext({ ...INPUTS, generating: true, narratorOnlyMode: true });
    expect(ctx).toMatchObject({
      folder: 'dracula',
      mode: 'read',
      speakers: INPUTS.speakers,
      selected: INPUTS.selected,
      selectedItems: INPUTS.selectedItems,
      narratorOnlyMode: true,
      generating: true,
      blocked: null,
    });
  });

  it('reads an unknown folder as empty', () => {
    expect(rowContext({ ...INPUTS, folder: null }).folder).toBe('');
  });

  it.each([
    ['paragraphs', true, false],
    ['items', false, true],
  ] as const)(
    'a %s selection makes those rows selectable and not the others',
    (kind, selectable, itemSelectable) => {
      expect(rowContext({ ...INPUTS, kind })).toMatchObject({ selectable, itemSelectable });
    },
  );

  it("passes the page's Edit gate to the rows as blocked", () => {
    const block = { reason: STALE_READ_ONLY };
    expect(rowContext({ ...INPUTS, block }).blocked).toBe(block);
  });

  it('turns item play off while a History step is viewed, as takes are not kept there', () => {
    expect(
      rowContext({ ...INPUTS, viewing: true, block: { reason: VIEWING_READ_ONLY } }).playBlock,
    ).toEqual({ reason: ITEM_AUDIO_NOT_KEPT });
  });

  it('keeps item play on while stale or saving: the play gate is not the Edit gate', () => {
    for (const reason of [STALE_READ_ONLY, null]) {
      expect(rowContext({ ...INPUTS, block: { reason } }).playBlock).toBeNull();
    }
  });

  it('lights marks only while the Pronunciations panel is open', () => {
    expect(rowContext(INPUTS).litMarks.size).toBe(0);
    expect(rowContext({ ...INPUTS, pronunciationsOpen: true }).litMarks).toBe(LIT);
  });
});
