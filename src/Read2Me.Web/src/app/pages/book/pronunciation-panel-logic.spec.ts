import { describe, expect, it } from 'bun:test';
import { indexSpans } from './pronunciation-marks';
import { panelRows, selectionPill } from './pronunciation-panel-logic';

const HOLMES = { id: 'holmes', writtenForm: 'Holmes', spokenForm: 'HOHMZ' };
const WATSON = { id: 'watson', writtenForm: 'Watson', spokenForm: 'WAT-sun' };

const span = (itemId: string, start: number, pronunciationId: string) => ({
  itemId,
  start,
  length: 3,
  pronunciationId,
  sentenceStart: 0,
});

describe('the panel rows', () => {
  it('list each entry in the loaded chapters once, in order of first match, with its count and items with audio', () => {
    const marks = indexSpans({
      pronunciations: [WATSON, HOLMES],
      spans: [
        span('i1', 0, 'holmes'),
        span('i1', 10, 'holmes'),
        span('i2', 4, 'watson'),
        span('i3', 0, 'holmes'),
      ],
    });
    const withAudio = new Set(['i1', 'i2']);

    const rows = panelRows(marks, (id) => withAudio.has(id));

    expect(rows.map((r) => [r.entry.writtenForm, r.count, r.withAudio])).toEqual([
      ['Holmes', 3, 1],
      ['Watson', 1, 1],
    ]);
  });

  it('remember where each entry first matches, the place a row opens the editor from', () => {
    const marks = indexSpans({
      pronunciations: [HOLMES],
      spans: [span('i2', 7, 'holmes'), span('i3', 0, 'holmes')],
    });

    expect(panelRows(marks, () => false)[0]!.first).toEqual({
      itemId: 'i2',
      offset: 7,
    });
  });

  it('are none when nothing in the loaded chapters matches', () => {
    expect(panelRows(indexSpans({ pronunciations: [HOLMES], spans: [] }), () => false)).toEqual([]);
  });
});

const ENTRIES = [
  { ...HOLMES, matchCount: 3, itemCount: 2, withAudioCount: 0 },
  {
    id: 'van-helsing',
    writtenForm: 'Van Helsing',
    spokenForm: 'van HEL-sing',
    matchCount: 1,
    itemCount: 1,
    withAudioCount: 0,
  },
];

describe('the selection pill', () => {
  it('offers Add for a word not in the list, pre-filling written and spoken with it', () => {
    expect(selectionPill('Watson', ENTRIES)).toEqual({
      action: 'add',
      entry: null,
      forms: { writtenForm: 'Watson', spokenForm: 'Watson' },
      lead: 0,
    });
  });

  it('offers Edit when the selection is a written form, ignoring case and spacing', () => {
    expect(selectionPill('HOLMES', ENTRIES)).toMatchObject({
      action: 'edit',
      entry: { id: 'holmes' },
    });
    expect(selectionPill('van\n  helsing', ENTRIES)).toMatchObject({
      action: 'edit',
      entry: { id: 'van-helsing' },
    });
  });

  it('takes up to four words and no more', () => {
    expect(selectionPill('the castle of Dracula', ENTRIES)?.action).toBe('add');
    expect(selectionPill('up the castle of Dracula', ENTRIES)).toBeNull();
  });

  it('drops the punctuation and space a drag catches at either end, saying how much it dropped in front', () => {
    expect(selectionPill(' “Watson’s,', ENTRIES)).toMatchObject({
      forms: { writtenForm: 'Watson’s', spokenForm: 'Watson’s' },
      lead: 2,
    });
  });

  it('offers nothing for an empty or wordless selection', () => {
    expect(selectionPill('', ENTRIES)).toBeNull();
    expect(selectionPill(' — ', ENTRIES)).toBeNull();
  });

  it('offers nothing for a selection longer than a written form may be', () => {
    expect(selectionPill('a'.repeat(101), ENTRIES)).toBeNull();
  });
});
