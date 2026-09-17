import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CharacterSummaryDto, CharacterVoicesDto, VoiceDto } from '@app/api';
import { Receipt, ReceiptMessage, VoiceBatchMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subject } from 'rxjs';
import { REFETCH_DEBOUNCE_MS } from '../project/project-store';
import { CastStore } from './cast-store';

class FakeLive {
  readonly receipts = new Subject<Receipt>();
  readonly voiceBatch = new Subject<VoiceBatchMessage>();
  receipts$() {
    return this.receipts.asObservable();
  }
  on(family: string) {
    if (family !== 'voiceBatch') throw new Error(`unexpected family ${family}`);
    return this.voiceBatch.asObservable();
  }
}

const VOICES = '/api/projects/dune/characters/alice/voices';
const LINES = '/api/projects/dune/characters/alice/lines';

function voiceList(voices: Partial<VoiceDto>[]): CharacterVoicesDto {
  return {
    defaultVoiceId: null,
    voices: voices.map((v) => ({
      id: 'v1',
      characterId: 'alice',
      name: 'Main',
      description: null,
      source: 'Generated',
      designPrompt: null,
      transcript: null,
      audioFileName: null,
      isEdited: false,
      voiceDesignSettingsOverrideJson: null,
      ttsSettingsOverrideJson: null,
      ...v,
    })),
  };
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

  it('selecting a character loads its lines and voices; the selection follows the roster', async () => {
    await open();
    const selecting = store.select('alice');
    http
      .expectOne({ method: 'GET', url: LINES })
      .flush([{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'Hi' }]);
    http.expectOne({ method: 'GET', url: VOICES }).flush(voiceList([{ name: 'Main' }]));
    await selecting;
    expect(store.selected()?.name).toBe('Alice');
    expect(store.lines().map((l) => l.text)).toEqual(['Hi']);
    expect(store.voices().voices.map((v) => v.name)).toEqual(['Main']);

    await store.select(null);
    expect(store.selected()).toBeNull();
    expect(store.lines()).toEqual([]);
    expect(store.voices().voices).toEqual([]);
  });

  it('a cast-facet receipt reloads the roster and the selected lines and voices once per burst', async () => {
    await open();
    const selecting = store.select('alice');
    http.expectOne(LINES).flush([]);
    http.expectOne(VOICES).flush(voiceList([]));
    await selecting;

    live.receipts.next(receipt('Characters'));
    live.receipts.next(receipt('Attribution, Audio'));
    http.expectNone(SUMMARY);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectOne(SUMMARY).flush([row('Narrator'), row('Alice', 3)]);
    http.expectOne(LINES).flush([]);
    http.expectOne(VOICES).flush(voiceList([{ name: 'New' }]));
    await settle();
    expect(store.selected()?.lineCount).toBe(3);
    expect(store.voices().voices.map((v) => v.name)).toEqual(['New']);
  });

  it('a batch voiceUpdated patches the selected voice in place and bumps its audio version', async () => {
    await open();
    const selecting = store.select('alice');
    http.expectOne(LINES).flush([]);
    http.expectOne(VOICES).flush(voiceList([{ id: 'v1' }, { id: 'v2', name: 'Other' }]));
    await selecting;

    live.voiceBatch.next({
      kind: 'voiceUpdated',
      characterId: 'alice',
      voiceId: 'v1',
      designPrompt: 'warm alto',
      audioFileName: 'voices/alice/v1.wav',
    });
    expect(store.voices().voices[0]?.designPrompt).toBe('warm alto');
    expect(store.voices().voices[0]?.audioFileName).toBe('voices/alice/v1.wav');
    expect(store.audioVersions()['v1']).toBe(1);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectNone(VOICES);

    // Another character's voice is not this panel's business.
    live.voiceBatch.next({
      kind: 'voiceUpdated',
      characterId: 'bob',
      voiceId: 'v9',
      designPrompt: 'x',
    });
    expect(store.voices().voices.map((v) => v.designPrompt)).toEqual(['warm alto', null]);
  });

  it('a voiceUpdated for a voice this tab has not seen, and a completed batch, reload', async () => {
    await open();
    const selecting = store.select('alice');
    http.expectOne(LINES).flush([]);
    http.expectOne(VOICES).flush(voiceList([]));
    await selecting;

    live.voiceBatch.next({
      kind: 'voiceUpdated',
      characterId: 'alice',
      voiceId: 'v-new',
      designPrompt: 'x',
    });
    live.voiceBatch.next({ kind: 'completed', processed: 1, failed: 0 });
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectOne(SUMMARY).flush([row('Narrator'), row('Alice', 2)]);
    http.expectOne(LINES).flush([]);
    http.expectOne(VOICES).flush(voiceList([{ id: 'v-new', designPrompt: 'x' }]));
    await settle();
    expect(store.voices().voices.map((v) => v.id)).toEqual(['v-new']);
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
