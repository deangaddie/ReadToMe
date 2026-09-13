import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectDetailDto, ProjectStatusDto } from '@app/api';
import {
  ItemStatusMessage,
  LiveSnapshot,
  NodeStatusMessage,
  NodeStatusSummary,
  ProjectSnapshot,
  Receipt,
  ReceiptMessage,
} from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ProjectTitles } from '@app/shell/project-titles';
import { Subject } from 'rxjs';
import { ProjectStore, REFETCH_DEBOUNCE_MS } from './project-store';

function summary(overrides: Partial<NodeStatusSummary> = {}): NodeStatusSummary {
  return {
    attributionRemaining: 1,
    audioRemaining: 1,
    review: 0,
    attributionProcessing: false,
    attributionQueued: 0,
    isDone: false,
    ...overrides,
  };
}

const DETAIL: ProjectDetailDto = {
  folderName: 'dune',
  title: 'Dune',
  bookTitle: 'Dune',
  author: 'Frank Herbert',
  filename: 'dune.epub',
  fileType: 'Epub',
  coverImage: null,
  narratorOnlyMode: false,
  narrator: { characterId: 'n', displayName: 'Narrator', isLinked: false },
};

function statusDto(overrides: Partial<ProjectStatusDto> = {}): ProjectStatusDto {
  return {
    hasContent: true,
    characters: 2,
    charactersWithLines: 2,
    readyVoices: 0,
    items: { total: 4, withAudio: 0, unattributed: 2 },
    attribution: { remaining: 2, processing: false, queued: 0 },
    audio: { remaining: 2 },
    review: 0,
    volumeIds: ['v1'],
    nodes: { v1: summary({ attributionRemaining: 2, audioRemaining: 2 }), c1: summary() },
    revision: 5,
    ...overrides,
  };
}

class FakeLive {
  readonly nodeStatus = new Subject<NodeStatusMessage>();
  readonly itemStatus = new Subject<ItemStatusMessage>();
  readonly receipts = new Subject<Receipt>();
  readonly resynced = new Subject<LiveSnapshot>();
  readonly projects = signal<Record<string, ProjectSnapshot>>({});
  readonly joined: string[] = [];
  readonly left: string[] = [];
  readonly resynced$ = this.resynced.asObservable();

  on(family: string) {
    if (family === 'nodeStatus') return this.nodeStatus.asObservable();
    if (family === 'itemStatus') return this.itemStatus.asObservable();
    throw new Error(`unexpected family ${family}`);
  }
  receipts$() {
    return this.receipts.asObservable();
  }
  async joinProject(folder: string) {
    this.joined.push(folder);
  }
  async leaveProject(folder: string) {
    this.left.push(folder);
  }
}

function receipt(revision: number, facets: string): Receipt {
  const message: ReceiptMessage = {
    folder: 'dune',
    mutationName: 'X',
    mutationId: 'm',
    revision,
    effects: {
      scope: 'Exact',
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: facets === 'None',
    },
    originId: 'o',
  };
  return { ...message, isOwn: false };
}

describe('ProjectStore', () => {
  let http: HttpTestingController;
  let live: FakeLive;
  let store: ProjectStore;

  beforeEach(() => {
    live = new FakeLive();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ProjectStore,
        { provide: LiveService, useValue: live },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ProjectStore);
  });

  afterEach(() => {
    store.close();
    http.verify();
  });

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function open(status = statusDto()) {
    const opened = store.open('dune');
    http.expectOne({ method: 'GET', url: '/api/projects/dune' }).flush(DETAIL);
    http.expectOne({ method: 'GET', url: '/api/projects/dune/status' }).flush(status);
    await opened;
  }

  it('loads detail + status, joins the project group and names the breadcrumb', async () => {
    await open();

    expect(live.joined).toEqual(['dune']);
    expect(store.detail()?.title).toBe('Dune');
    expect(store.status()?.revision).toBe(5);
    expect(store.revision()).toBe(5);
    expect(store.nodes()['v1']?.attributionRemaining).toBe(2);
    expect(store.folderAudioRemaining()).toBe(2);
    expect(TestBed.inject(ProjectTitles).titles()['dune']).toBe('Dune');
  });

  it('applies nodeStatus deltas: changed nodes replaced, null removes, folder remaining taken', async () => {
    await open();

    live.nodeStatus.next({
      folder: 'DUNE',
      nodes: {
        v1: summary({ attributionRemaining: 0, attributionQueued: 3 }),
        c1: null,
        c2: summary(),
      },
      folderAudioRemaining: 1,
    });

    expect(store.nodes()['v1']?.attributionQueued).toBe(3);
    expect('c1' in store.nodes()).toBe(false);
    expect(store.nodes()['c2']).toBeDefined();
    expect(store.folderAudioRemaining()).toBe(1);
  });

  it('ignores deltas for another folder', async () => {
    await open();
    live.nodeStatus.next({ folder: 'emma', nodes: { v1: null }, folderAudioRemaining: 0 });
    expect(store.nodes()['v1']).toBeDefined();
    expect(store.folderAudioRemaining()).toBe(2);
  });

  it('applies itemStatus deltas and counts audio items in flight', async () => {
    await open();

    live.itemStatus.next({
      folder: 'dune',
      paragraphs: { p1: { status: 'Queued' } },
      items: { i1: { status: 'Queued' }, i2: { status: 'Processing' }, i3: { audioVersion: 2 } },
    });
    expect(store.audioInFlight()).toBe(2);

    live.itemStatus.next({ folder: 'dune', paragraphs: { p1: null }, items: { i1: null } });
    expect(store.audioInFlight()).toBe(1);
    expect(store.paragraphs()['p1']).toBeUndefined();
  });

  it('a Structure receipt refetches status once, debounced, and takes the new revision', async () => {
    await open();

    live.receipts.next(receipt(6, 'Structure'));
    live.receipts.next(receipt(7, 'Structure, Audio'));
    expect(store.revision()).toBe(7);
    http.expectNone('/api/projects/dune/status');

    await settle(REFETCH_DEBOUNCE_MS + 20);
    http
      .expectOne({ method: 'GET', url: '/api/projects/dune/status' })
      .flush(statusDto({ revision: 7, hasContent: false }));
    await settle();
    expect(store.status()?.hasContent).toBe(false);
  });

  it('a receipt that only touches voice rules neither refetches nor gaps', async () => {
    await open();
    live.receipts.next(receipt(6, 'VoiceRules'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectNone('/api/projects/dune/status');
    http.expectNone('/api/projects/dune');
  });

  it('a revision gap refetches status even for an unrelated facet', async () => {
    await open();
    live.receipts.next(receipt(9, 'VoiceRules'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectOne('/api/projects/dune/status').flush(statusDto({ revision: 9 }));
    await settle();
  });

  it('Characters / Narrator / ProjectPolicy receipts refetch the detail', async () => {
    await open();
    live.receipts.next(receipt(6, 'ProjectPolicy'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http
      .expectOne({ method: 'GET', url: '/api/projects/dune' })
      .flush({ ...DETAIL, narratorOnlyMode: true });
    await settle();
    expect(store.detail()?.narratorOnlyMode).toBe(true);
  });

  it('a resync whose project revision is ahead refetches status', async () => {
    await open();
    live.resynced.next({ projects: { dune: { revision: 8 } } } as unknown as LiveSnapshot);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectOne('/api/projects/dune/status').flush(statusDto({ revision: 8 }));
    await settle();

    live.resynced.next({ projects: { dune: { revision: 8 } } } as unknown as LiveSnapshot);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    http.expectNone('/api/projects/dune/status');
  });

  it('close leaves the group and stops listening', async () => {
    await open();
    store.close();
    expect(live.left).toEqual(['dune']);
    live.nodeStatus.next({ folder: 'dune', nodes: { v1: null }, folderAudioRemaining: 0 });
    expect(store.nodes()['v1']).toBeDefined();
  });

  it('surfaces a load failure as an error', async () => {
    const opened = store.open('dune');
    http
      .expectOne('/api/projects/dune')
      .flush({ detail: 'gone' }, { status: 404, statusText: 'Not Found' });
    http
      .expectOne('/api/projects/dune/status')
      .flush(null, { status: 404, statusText: 'Not Found' });
    await opened;
    expect(store.error()).toBeTruthy();
    expect(store.detail()).toBeNull();
  });
});
