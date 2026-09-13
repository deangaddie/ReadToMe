import { Receipt } from '@app/live/live-messages';
import { LoadedChapter, ReceiptContext, planReceipt } from './receipt-plan';

function receipt(
  overrides: Partial<Receipt['effects']> & { revision?: number; isOwn?: boolean },
): Receipt {
  const { revision = 6, isOwn = false, ...effects } = overrides;
  return {
    folder: 'dune',
    mutationName: 'X',
    mutationId: 'm',
    revision,
    originId: 'o',
    isOwn,
    effects: {
      scope: 'Exact',
      facets: 'None',
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
      ...effects,
    },
  };
}

const CH1: LoadedChapter = {
  chapterId: 'c1',
  paragraphIds: new Set(['p1', 'p2']),
  itemIds: new Set(['i1', 'i2']),
};
const CH2: LoadedChapter = {
  chapterId: 'c2',
  paragraphIds: new Set(['p3']),
  itemIds: new Set(['i3']),
};

function ctx(overrides: Partial<ReceiptContext> = {}): ReceiptContext {
  return { lastRevision: 5, loaded: [CH1, CH2], visibleChapterId: 'c1', ...overrides };
}

describe('planReceipt', () => {
  it.each([
    ['ItemText on a paragraph', { facets: 'ItemText', paragraphIds: ['p3'] }, ['c2']],
    ['Attribution on an item', { facets: 'Attribution', paragraphItemIds: ['i1'] }, ['c1']],
    ['Audio on an item', { facets: 'Audio', paragraphItemIds: ['i3'] }, ['c2']],
    ['Reviews on an item', { facets: 'Reviews', paragraphItemIds: ['i2'] }, ['c1']],
    ['NodeTitle on a loaded chapter', { facets: 'NodeTitle', nodeIds: ['c2'] }, ['c2']],
    ['Structure across both', { facets: 'Structure', paragraphIds: ['p1', 'p3'] }, ['c1', 'c2']],
    ['a combined facet string', { facets: 'Attribution, Audio', paragraphItemIds: ['i1'] }, ['c1']],
    ['an unloaded paragraph', { facets: 'ItemText', paragraphIds: ['p9'] }, []],
    ['a facet the reader does not render', { facets: 'ProjectPolicy', paragraphIds: ['p1'] }, []],
  ])('%s reloads %j', (_label, effects, chapters) => {
    expect(planReceipt(receipt(effects), ctx()).reloadChapters).toEqual(chapters);
  });

  it('a WholeProject receipt with a content facet reloads the visible chapter only', () => {
    const plan = planReceipt(receipt({ scope: 'WholeProject', facets: 'Audio' }), ctx());
    expect(plan.reloadChapters).toEqual(['c1']);
    expect(plan.dropOtherChapters).toBe(true);
  });

  it('a WholeProject receipt with nothing visible reloads nothing', () => {
    const plan = planReceipt(
      receipt({ scope: 'WholeProject', facets: 'Audio' }),
      ctx({ visibleChapterId: null }),
    );
    expect(plan.reloadChapters).toEqual([]);
  });

  it('Structure and NodeTitle reload the overview so the tree follows', () => {
    expect(planReceipt(receipt({ facets: 'Structure' }), ctx()).reloadOverview).toBe(true);
    expect(planReceipt(receipt({ facets: 'NodeTitle' }), ctx()).reloadOverview).toBe(true);
    expect(planReceipt(receipt({ facets: 'Attribution' }), ctx()).reloadOverview).toBe(false);
  });

  it('Characters and Narrator reload the overview (roster names) and every loaded voice map', () => {
    for (const facets of ['Characters', 'Narrator']) {
      const plan = planReceipt(receipt({ facets }), ctx());
      expect(plan.reloadOverview).toBe(true);
      expect(plan.reloadVoices).toEqual(['c1', 'c2']);
    }
  });

  it('Voices and VoiceRules reload loaded voice maps but not chapters', () => {
    const plan = planReceipt(receipt({ facets: 'Voices, VoiceRules' }), ctx());
    expect(plan.reloadVoices).toEqual(['c1', 'c2']);
    expect(plan.reloadChapters).toEqual([]);
    expect(plan.reloadOverview).toBe(false);
  });

  it('Reviews reloads the review map', () => {
    expect(planReceipt(receipt({ facets: 'Reviews' }), ctx()).reloadReviews).toBe(true);
    expect(planReceipt(receipt({ facets: 'Audio' }), ctx()).reloadReviews).toBe(false);
  });

  it.each<[string, Parameters<typeof receipt>[0], boolean]>([
    ['foreign Structure', { facets: 'Structure', isOwn: false }, true],
    ['own Structure', { facets: 'Structure', isOwn: true }, false],
    ['foreign speaker change', { facets: 'Attribution', isOwn: false }, false],
    [
      'foreign split relation',
      { facets: 'ItemText', structural: [{ kind: 'Split', sourceId: 'p1', resultId: 'p9' }] },
      true,
    ],
  ])('toast for %s: %s', (_label, effects, toast) => {
    expect(planReceipt(receipt(effects), ctx()).toast).toBe(toast);
  });

  it('a receipt that changed nothing plans nothing', () => {
    const plan = planReceipt(
      receipt({ facets: 'Structure', paragraphIds: ['p1'], changedNothing: true }),
      ctx(),
    );
    expect(plan).toMatchObject({
      reloadChapters: [],
      reloadOverview: false,
      toast: false,
      gap: false,
    });
  });

  describe('revision gap', () => {
    it('a skipped revision reloads the overview, reviews and the visible chapter', () => {
      const plan = planReceipt(receipt({ revision: 8, facets: 'None' }), ctx());
      expect(plan).toMatchObject({
        gap: true,
        reloadOverview: true,
        reloadReviews: true,
        reloadChapters: ['c1'],
        dropOtherChapters: true,
      });
    });

    it('the next revision is not a gap', () => {
      expect(planReceipt(receipt({ revision: 6 }), ctx()).gap).toBe(false);
    });

    it('an old or repeated revision is not a gap', () => {
      expect(planReceipt(receipt({ revision: 5 }), ctx()).gap).toBe(false);
      expect(planReceipt(receipt({ revision: 3 }), ctx()).gap).toBe(false);
    });

    it('no baseline yet (revision unknown) is not a gap', () => {
      expect(planReceipt(receipt({ revision: 40 }), ctx({ lastRevision: null })).gap).toBe(false);
    });

    it('a gap still honours the receipt own facets', () => {
      const plan = planReceipt(
        receipt({ revision: 9, facets: 'ItemText', paragraphIds: ['p3'] }),
        ctx(),
      );
      expect(plan.reloadChapters).toEqual(['c1', 'c2']);
    });
  });
});
