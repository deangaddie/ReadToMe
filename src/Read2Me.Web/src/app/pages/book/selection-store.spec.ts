import { SelectionStore } from './selection-store';

const A = { chapterId: 'c1', partId: 'pt1', volumeId: 'v1' };

describe('SelectionStore', () => {
  let store: SelectionStore;

  beforeEach(() => {
    store = new SelectionStore();
  });

  it('toggles rows and answers count, ids and membership', () => {
    store.toggle('p1', A, true);
    store.toggle('p2', A, true);
    expect(store.count()).toBe(2);
    expect(store.has('p1')).toBe(true);
    expect(store.selected().has('p2')).toBe(true);

    store.toggle('p1', A, false);
    store.toggle('nope', A, false);
    expect(store.ids()).toEqual(['p2']);
  });

  it('adds a node read as refs, removes by id and clears', () => {
    store.add([
      { id: 'p1', chapterId: 'c1', partId: 'pt1', volumeId: 'v1' },
      { id: 'p2', chapterId: 'c2', partId: 'pt1', volumeId: 'v1' },
    ]);
    expect(store.selection()['p2']).toEqual({ chapterId: 'c2', partId: 'pt1', volumeId: 'v1' });

    store.remove(['p1']);
    expect(store.ids()).toEqual(['p2']);
    store.clear();
    expect(store.count()).toBe(0);
  });

  it('a node is checked only once its total is known and reached', () => {
    store.toggle('p1', A, true);
    expect(store.nodeState('chapter', 'c1')).toBe('indeterminate');
    expect(store.nodeState('part', 'pt1')).toBe('indeterminate');
    expect(store.nodeState('chapter', 'c2')).toBe('unchecked');

    store.learnTotals({ c1: 1, pt1: 3 });
    expect(store.nodeState('chapter', 'c1')).toBe('checked');
    expect(store.nodeState('part', 'pt1')).toBe('indeterminate');
  });

  it('reset forgets the selection and the totals', () => {
    store.toggle('p1', A, true);
    store.learnTotals({ c1: 1 });
    store.reset();
    expect(store.count()).toBe(0);
    expect(store.totals()).toEqual({});
  });
});
