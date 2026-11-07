import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { PronunciationSpanReport, BookFacets } from '@app/api';
import { resetServices } from '@app/core/services';
import type { Receipt } from '@app/live/live-messages';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { FakeTimers } from '../../../testing/fake-timers';
import { PronunciationMarks } from './pronunciation-marks-store';

const MATCHES = '/api/projects/the-adventures-of-sherlock-holmes/pronunciations/matches';

function receipt(facets: BookFacets[], folder = 'the-adventures-of-sherlock-holmes'): Receipt {
  return {
    folder,
    mutationName: 'SetPronunciationMutation',
    mutationId: 'm',
    revision: 2,
    effects: {
      scope: 'Exact',
      createdId: null,
      createdParentId: null,
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

let api: FakeApi;
let live: FakeLive;
let timers: FakeTimers;
let report: PronunciationSpanReport;
let marks: PronunciationMarks;

/**
 * Runs the refetch debounce out on the fake clock and lets its read land. Afterwards no timer is
 * left, so "no read happened" is exact: nothing was scheduled, rather than nothing yet.
 */
async function debounceRunsOut() {
  await timers.advance(REFETCH_DEBOUNCE_MS);
  expect(timers.pending).toBe(0);
}

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  timers = new FakeTimers().install();
  report = {
    pronunciations: [{ id: 'holmes', writtenForm: 'Holmes', spokenForm: 'HOHMZ' }],
    spans: [
      {
        itemId: 'i1',
        start: 0,
        length: 6,
        pronunciationId: 'holmes',
        sentenceStart: 0,
      },
    ],
  };
  api.on('GET', MATCHES, () => report);
  marks = new PronunciationMarks();
});

afterEach(() => {
  marks.close();
  timers.restore();
});

describe('PronunciationMarks', () => {
  it('reads the spans of the chapters in view, once per burst', async () => {
    marks.follow('the-adventures-of-sherlock-holmes', ['c1']);
    marks.follow('the-adventures-of-sherlock-holmes', ['c1', 'c2']);
    await debounceRunsOut();

    const calls = api.calls('GET', MATCHES);
    expect(calls.map((c) => c.search)).toEqual(['?chapterIds=c1&chapterIds=c2']);
    expect(marks.marks().byItem['i1']?.[0]?.pronunciationId).toBe('holmes');
    expect(marks.marks().entries['holmes']?.spokenForm).toBe('HOHMZ');
  });

  it('reads again on a Pronunciations receipt for its project, and not on others', async () => {
    marks.follow('the-adventures-of-sherlock-holmes', ['c1']);
    await debounceRunsOut();

    live.emit('receipt', receipt(['Structure']));
    live.emit('receipt', receipt(['Pronunciations'], 'other'));
    expect(timers.pending).toBe(0);
    await debounceRunsOut();
    expect(api.calls('GET', MATCHES)).toHaveLength(1);

    report = { pronunciations: [], spans: [] };
    live.emit('receipt', receipt(['Characters', 'Pronunciations']));
    await debounceRunsOut();
    expect(api.calls('GET', MATCHES)).toHaveLength(2);
    expect(marks.marks().byItem).toEqual({});
  });

  it('shows nothing and reads nothing without chapters in view', async () => {
    marks.follow('the-adventures-of-sherlock-holmes', ['c1']);
    await debounceRunsOut();
    marks.follow('the-adventures-of-sherlock-holmes', []);
    await debounceRunsOut();

    expect(api.calls('GET', MATCHES)).toHaveLength(1);
    expect(marks.marks().byItem).toEqual({});
  });

  it('keeps the marks it has when a read fails, and stops listening once closed', async () => {
    marks.follow('the-adventures-of-sherlock-holmes', ['c1']);
    await debounceRunsOut();
    api.on('GET', MATCHES, () => problem(503, 'down'));
    live.emit('receipt', receipt(['Pronunciations']));
    await debounceRunsOut();
    expect(api.calls('GET', MATCHES)).toHaveLength(2);
    expect(marks.marks().byItem['i1']).toHaveLength(1);

    marks.close();
    live.emit('receipt', receipt(['Pronunciations']));
    expect(timers.pending).toBe(0);
    await debounceRunsOut();
    expect(api.calls('GET', MATCHES)).toHaveLength(2);
    expect(marks.marks().byItem).toEqual({});
  });
});
