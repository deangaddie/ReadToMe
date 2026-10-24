import { describe, expect, it } from 'bun:test';
import { indexSpans } from './pronunciation-marks';
import { panelRows, selectionPill } from './pronunciation-panel-logic';

const PUG = { id: 'pug', writtenForm: 'Pug', spokenForm: 'PUHG' };
const KULGAN = { id: 'kulgan', writtenForm: 'Kulgan', spokenForm: 'KULL-gun' };

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
      pronunciations: [KULGAN, PUG],
      spans: [
        span('i1', 0, 'pug'),
        span('i1', 10, 'pug'),
        span('i2', 4, 'kulgan'),
        span('i3', 0, 'pug'),
      ],
    });
    const withAudio = new Set(['i1', 'i2']);

    const rows = panelRows(marks, (id) => withAudio.has(id));

    expect(rows.map((r) => [r.entry.writtenForm, r.count, r.withAudio])).toEqual([
      ['Pug', 3, 1],
      ['Kulgan', 1, 1],
    ]);
  });

  it('remember where each entry first matches, the place a row opens the editor from', () => {
    const marks = indexSpans({
      pronunciations: [PUG],
      spans: [span('i2', 7, 'pug'), span('i3', 0, 'pug')],
    });

    expect(panelRows(marks, () => false)[0]!.first).toEqual({
      itemId: 'i2',
      offset: 7,
    });
  });

  it('are none when nothing in the loaded chapters matches', () => {
    expect(panelRows(indexSpans({ pronunciations: [PUG], spans: [] }), () => false)).toEqual([]);
  });
});

const ENTRIES = [
  { ...PUG, matchCount: 3, itemCount: 2, withAudioCount: 0 },
  {
    id: 'minas',
    writtenForm: 'Minas Tirith',
    spokenForm: 'MEE-nas TEER-ith',
    matchCount: 1,
    itemCount: 1,
    withAudioCount: 0,
  },
];

describe('the selection pill', () => {
  it('offers Add for a word not in the list, pre-filling written and spoken with it', () => {
    expect(selectionPill('Kulgan', ENTRIES)).toEqual({
      action: 'add',
      entry: null,
      forms: { writtenForm: 'Kulgan', spokenForm: 'Kulgan' },
      lead: 0,
    });
  });

  it('offers Edit when the selection is a written form, ignoring case and spacing', () => {
    expect(selectionPill('PUG', ENTRIES)).toMatchObject({
      action: 'edit',
      entry: { id: 'pug' },
    });
    expect(selectionPill('minas\n  tirith', ENTRIES)).toMatchObject({
      action: 'edit',
      entry: { id: 'minas' },
    });
  });

  it('takes up to four words and no more', () => {
    expect(selectionPill('the tower of Ecthelion', ENTRIES)?.action).toBe('add');
    expect(selectionPill('up the tower of Ecthelion', ENTRIES)).toBeNull();
  });

  it('drops the punctuation and space a drag catches at either end, saying how much it dropped in front', () => {
    expect(selectionPill(' “Kulgan’s,', ENTRIES)).toMatchObject({
      forms: { writtenForm: 'Kulgan’s', spokenForm: 'Kulgan’s' },
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
