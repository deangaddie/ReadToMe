import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CharacterSummaryDto } from '@app/api';
import { Receipt, ReceiptMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subject } from 'rxjs';
import { REFETCH_DEBOUNCE_MS } from '../project/project-store';
import { CastStore } from './cast-store';

class FakeLive {
  readonly receipts = new Subject<Receipt>();
  receipts$() {
    return this.receipts.asObservable();
  }
}

class FakeToast {
  readonly problems: unknown[] = [];
  problem(p: unknown) {
    this.problems.push(p);
  }
}

function row(name: string, lineCount = 0): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount,
    voiceCount: 0,
    readyVoiceCount: 0,
    isNarrator: name === 'Narrator',
    narratesBook: false,
  };
}

function receipt(facets: string): Receipt {
  const message: ReceiptMessage = {
    folder: 'dune',
    mutationName: 'X',
    mutationId: 'm',
    revision: 1,
    effects: {
      scope: 'Exact',
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
    originId: 'o',
  };
  return { ...message, isOwn: false };
}

const SUMMARY = '/api/projects/dune/characters/summary';

describe('CastStore', () => {
  let http: HttpTestingController;
  let live: FakeLive;
  let toast: FakeToast;
  let store: CastStore;

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  beforeEach(() => {
    live = new FakeLive();
    toast = new FakeToast();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        CastStore,
        { provide: LiveService, useValue: live },
        { provide: ToastService, useValue: toast },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CastStore);
  });

  afterEach(() => {
    store.close();
    http.verify();
  });

  async function open(rows = [row('Narrator'), row('Alice', 2)]) {
    const opened = store.open('dune');
    http.expectOne({ method: 'GET', url: SUMMARY }).flush(rows);
    await opened;
  }

  it('loads the roster', async () => {
    await open();
    expect(store.rows().map((r) => r.name)).toEqual(['Narrator', 'Alice']);
    expect(store.loading()).toBe(false);
    expect(store.selected()).toBeNull();
  });

  it('selecting a character loads its lines; the selection follows the roster', async () => {
    await open();
    const selecting = store.select('alice');
    http
      .expectOne({ method: 'GET', url: '/api/projects/dune/characters/alice/lines' })
      .flush([{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'Hi' }]);
    await selecting;
    expect(store.selected()?.name).toBe('Alice');
    expect(store.lines().map((l) => l.text)).toEqual(['Hi']);

    await store.select(null);
    expect(store.selected()).toBeNull();
    expect(store.lines()).toEqual([]);
  });

  it('a cast-facet receipt reloads the roster and the selected lines once per burst', async () => {
    await open();
    const selecting = store.select('alice');
    http.expectOne('/api/projects/dune/characters/alice/lines').flush([]);
    await selecting;

    live.receipts.next(receipt('Characters'));
    live.receipts.next(receipt('Attribution, Audio'));
    http.expectNone(SUMMARY);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectOne(SUMMARY).flush([row('Narrator'), row('Alice', 3)]);
    http.expectOne('/api/projects/dune/characters/alice/lines').flush([]);
    await settle();
    expect(store.selected()?.lineCount).toBe(3);
  });

  it('ignores receipts that touch nothing the roster shows', async () => {
    await open();
    live.receipts.next(receipt('ItemText, NodeTitle'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectNone(SUMMARY);
  });

  it('run posts the command, then reloads; the response is answered', async () => {
    await open();
    const running = store.run({ type: 'CreateCharacter', name: 'Bob' });
    expect(store.busy()).toBe(true);
    http
      .expectOne({ method: 'POST', url: '/api/projects/dune/commands' })
      .flush({ outcome: 'Committed', newEntityId: 'bob' });
    await settle();
    http.expectOne(SUMMARY).flush([row('Narrator'), row('Alice', 2), row('Bob')]);
    const response = await running;
    expect(response?.newEntityId).toBe('bob');
    expect(store.busy()).toBe(false);
    expect(store.rows().map((r) => r.name)).toContain('Bob');
  });

  it('a refused command is toasted and nothing reloads', async () => {
    await open();
    const running = store.run({ type: 'RenameCharacter', characterId: 'alice', name: '' });
    http
      .expectOne({ method: 'POST', url: '/api/projects/dune/commands' })
      .flush({ detail: 'Name is required.' }, { status: 422, statusText: 'Unprocessable' });
    expect(await running).toBeUndefined();
    expect(toast.problems).toHaveLength(1);
    http.expectNone(SUMMARY);
  });
});
