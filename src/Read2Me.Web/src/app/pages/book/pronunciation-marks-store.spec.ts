import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { PronunciationSpanReport } from '@app/api';
import { resetServices } from '@app/core/services';
import type { Receipt } from '@app/live/live-messages';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { PronunciationMarks } from './pronunciation-marks-store';

const MATCHES = '/api/projects/magician/pronunciations/matches';

function receipt(facets: string, folder = 'magician'): Receipt {
  return {
    folder,
    mutationName: 'SetPronunciationMutation',
    mutationId: 'm',
    revision: 2,
    effects: {
      scope: 'Exact',
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
    originId: 'elsewhere',
    isOwn: false,
  } as Receipt;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const settle = () => sleep(REFETCH_DEBOUNCE_MS + 30);

let api: FakeApi;
let live: FakeLive;
let report: PronunciationSpanReport;
let marks: PronunciationMarks;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  report = {
    pronunciations: [{ id: 'pug', writtenForm: 'Pug', spokenForm: 'PUHG' }],
    spans: [
      {
        itemId: 'i1',
        start: 0,
        length: 3,
        pronunciationId: 'pug',
        sentenceStart: 0,
      },
    ],
  };
  api.on('GET', MATCHES, () => report);
  marks = new PronunciationMarks();
});

afterEach(() => marks.close());

describe('PronunciationMarks', () => {
  it('reads the spans of the chapters in view, once per burst', async () => {
    marks.follow('magician', ['c1']);
    marks.follow('magician', ['c1', 'c2']);
    await settle();

    const calls = api.calls('GET', MATCHES);
    expect(calls.map((c) => c.search)).toEqual(['?chapterIds=c1&chapterIds=c2']);
    expect(marks.marks().byItem['i1']?.[0]?.pronunciationId).toBe('pug');
    expect(marks.marks().entries['pug']?.spokenForm).toBe('PUHG');
  });

  it('reads again on a Pronunciations receipt for its project, and not on others', async () => {
    marks.follow('magician', ['c1']);
    await settle();

    live.emit('receipt', receipt('Structure'));
    live.emit('receipt', receipt('Pronunciations', 'other'));
    await settle();
    expect(api.calls('GET', MATCHES)).toHaveLength(1);

    report = { pronunciations: [], spans: [] };
    live.emit('receipt', receipt('Characters, Pronunciations'));
    await settle();
    expect(api.calls('GET', MATCHES)).toHaveLength(2);
    expect(marks.marks().byItem).toEqual({});
  });

  it('shows nothing and reads nothing without chapters in view', async () => {
    marks.follow('magician', ['c1']);
    await settle();
    marks.follow('magician', []);
    await settle();

    expect(api.calls('GET', MATCHES)).toHaveLength(1);
    expect(marks.marks().byItem).toEqual({});
  });

  it('keeps the marks it has when a read fails, and stops listening once closed', async () => {
    marks.follow('magician', ['c1']);
    await settle();
    api.on('GET', MATCHES, () => problem(503, 'down'));
    live.emit('receipt', receipt('Pronunciations'));
    await settle();
    expect(marks.marks().byItem['i1']).toHaveLength(1);

    marks.close();
    live.emit('receipt', receipt('Pronunciations'));
    await settle();
    expect(api.calls('GET', MATCHES)).toHaveLength(2);
    expect(marks.marks().byItem).toEqual({});
  });
});
