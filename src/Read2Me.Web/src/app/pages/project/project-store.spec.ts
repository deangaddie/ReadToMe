import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  type ProjectDetailDto,
  type ProjectStatusDto,
  StepView,
  VIEWING_READ_ONLY,
} from '@app/api';
import { Emitter } from '@app/core/emitter';
import { IDLE_ASSEMBLY, IDLE_VOICE_BATCH } from '@app/live/live-state';
import { override, resetServices, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import type {
  AssemblyMessage,
  AssemblyState,
  ItemStatusMessage,
  LiveSnapshot,
  NodeStatusMessage,
  NodeStatusSummary,
  ProjectSnapshot,
  Receipt,
  ReceiptMessage,
  VoiceBatchState,
} from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { ProjectTitles } from '@app/shell/project-titles';
import { FakeApi, problem } from '../../../testing/fake-api';
import { ProjectStore, REFETCH_DEBOUNCE_MS, formatBuildDate } from './project-store';

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
    pauseParagraphs: 0,
    checkpoints: {},
    revision: 5,
    ...overrides,
  };
}

/** The slice of LiveService the store uses, over Emitters. */
class FakeLive {
  readonly nodeStatus = new Emitter<NodeStatusMessage>();
  readonly itemStatus = new Emitter<ItemStatusMessage>();
  readonly assemblyMessages = new Emitter<AssemblyMessage>();
  readonly assembly = signal<AssemblyState>(IDLE_ASSEMBLY);
  readonly voiceBatch = signal<VoiceBatchState>(IDLE_VOICE_BATCH);
  readonly receiptsEmitter = new Emitter<Receipt>();
  readonly resyncedEmitter = new Emitter<LiveSnapshot>();
  readonly projects = signal<Record<string, ProjectSnapshot>>({});
  readonly joined: string[] = [];
  readonly left: string[] = [];

  on(family: string, listener: (m: unknown) => void) {
    if (family === 'nodeStatus') return this.nodeStatus.subscribe(listener);
    if (family === 'itemStatus') return this.itemStatus.subscribe(listener);
    if (family === 'assembly') return this.assemblyMessages.subscribe(listener);
    throw new Error(`unexpected family ${family}`);
  }
  receipts(_folder: string, listener: (r: Receipt) => void) {
    return this.receiptsEmitter.subscribe(listener);
  }
  resynced(listener: (s: LiveSnapshot) => void) {
    return this.resyncedEmitter.subscribe(listener);
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

const DETAIL_URL = '/api/projects/dune';
const STATUS_URL = '/api/projects/dune/status';
const OUTPUTS_URL = '/api/projects/dune/assembly/outputs';
const FULL_BUILD = {
  fileName: 'dune.m4b',
  sizeBytes: 1,
  createdAt: '2026-09-19T10:00:00Z',
  isPartial: false,
};

describe('ProjectStore', () => {
  let api: FakeApi;
  let live: FakeLive;
  let store: ProjectStore;

  beforeEach(() => {
    resetServices();
    api = new FakeApi();
    api.install();
    live = new FakeLive();
    override(LiveService, live as unknown as LiveService);
    store = new ProjectStore();
  });

  afterEach(() => store.close());

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function open(status = statusDto(), outputs: unknown = []) {
    api.on('GET', DETAIL_URL, DETAIL);
    api.on('GET', STATUS_URL, status);
    api.on('GET', OUTPUTS_URL, outputs);
    await store.open('dune');
    await settle();
  }

  const exportStep = () => store.pipeline()?.find((s) => s.id === 'export');

  it('loads detail + status, joins the project group and names the breadcrumb', async () => {
    await open();

    expect(live.joined).toEqual(['dune']);
    expect(store.detail()?.title).toBe('Dune');
    expect(store.status()?.revision).toBe(5);
    expect(store.revision()).toBe(5);
    expect(store.nodes()['v1']?.attributionRemaining).toBe(2);
    expect(store.folderAudioRemaining()).toBe(2);
    expect(use(ProjectTitles).titles()['dune']).toBe('Dune');
  });

  it('applies nodeStatus deltas: changed nodes replaced, null removes, folder remaining taken', async () => {
    await open();

    live.nodeStatus.emit({
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
    live.nodeStatus.emit({ folder: 'emma', nodes: { v1: null }, folderAudioRemaining: 0 });
    expect(store.nodes()['v1']).toBeDefined();
    expect(store.folderAudioRemaining()).toBe(2);
  });

  it('applies itemStatus deltas and counts audio items in flight', async () => {
    await open();

    live.itemStatus.emit({
      folder: 'dune',
      paragraphs: { p1: { status: 'Queued' } },
      items: { i1: { status: 'Queued' }, i2: { status: 'Processing' }, i3: { audioVersion: 2 } },
    });
    expect(store.audioInFlight()).toBe(2);

    live.itemStatus.emit({ folder: 'dune', paragraphs: { p1: null }, items: { i1: null } });
    expect(store.audioInFlight()).toBe(1);
    expect(store.paragraphs()['p1']).toBeUndefined();
  });

  it('a Structure receipt refetches status once, debounced, and takes the new revision', async () => {
    await open();
    api.on('GET', STATUS_URL, statusDto({ revision: 7, hasContent: false }));

    live.receiptsEmitter.emit(receipt(6, 'Structure'));
    live.receiptsEmitter.emit(receipt(7, 'Structure, Audio'));
    expect(store.revision()).toBe(7);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(1);

    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(2);
    expect(store.status()?.hasContent).toBe(false);
  });

  it('a Checkpoints receipt refetches status, where the confirmed and skipped steps live', async () => {
    await open();
    api.on(
      'GET',
      STATUS_URL,
      statusDto({
        checkpoints: {
          structure: { state: 'confirmed', stale: false, at: '2026-10-17T09:00:00Z' },
        },
      }),
    );

    live.receiptsEmitter.emit(receipt(6, 'Checkpoints'));
    await settle(REFETCH_DEBOUNCE_MS + 20);

    expect(api.calls('GET', STATUS_URL)).toHaveLength(2);
    expect(store.status()?.checkpoints.structure?.state).toBe('confirmed');
  });

  it('a receipt that only touches voice rules neither refetches nor gaps', async () => {
    await open();
    live.receiptsEmitter.emit(receipt(6, 'VoiceRules'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(1);
    expect(api.calls('GET', DETAIL_URL)).toHaveLength(1);
  });

  it('a revision gap refetches status even for an unrelated facet', async () => {
    await open();
    api.on('GET', STATUS_URL, statusDto({ revision: 9 }));
    live.receiptsEmitter.emit(receipt(9, 'VoiceRules'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(2);
    expect(store.revision()).toBe(9);
  });

  it('Characters / Narrator / ProjectPolicy receipts refetch the detail', async () => {
    await open();
    api.on('GET', DETAIL_URL, { ...DETAIL, narratorOnlyMode: true });
    live.receiptsEmitter.emit(receipt(6, 'ProjectPolicy'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', DETAIL_URL)).toHaveLength(2);
    expect(store.detail()?.narratorOnlyMode).toBe(true);
  });

  it('a resync whose project revision is ahead refetches status', async () => {
    await open();
    api.on('GET', STATUS_URL, statusDto({ revision: 8 }));
    live.resyncedEmitter.emit({ projects: { dune: { revision: 8 } } } as unknown as LiveSnapshot);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(2);

    live.resyncedEmitter.emit({ projects: { dune: { revision: 8 } } } as unknown as LiveSnapshot);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', STATUS_URL)).toHaveLength(2);
  });

  describe('a resync while /status is in flight', () => {
    async function snapshotThenStatus(snapshot: number) {
      let answer!: (body: unknown) => void;
      api.on('GET', DETAIL_URL, DETAIL);
      api.on('GET', STATUS_URL, () => new Promise((r) => (answer = r)));
      const opened = store.open('dune');
      await settle();
      live.resyncedEmitter.emit({
        projects: { dune: { revision: snapshot } },
      } as unknown as LiveSnapshot);
      answer(statusDto());
      await opened;
      api.on('GET', STATUS_URL, statusDto({ revision: snapshot }));
      await settle(REFETCH_DEBOUNCE_MS + 20);
    }

    it('reads status once when the snapshot matches what /status read', async () => {
      await snapshotThenStatus(5);
      expect(api.calls('GET', STATUS_URL)).toHaveLength(1);
    });

    it('refetches status when the snapshot is ahead of what /status read', async () => {
      await snapshotThenStatus(8);
      expect(api.calls('GET', STATUS_URL)).toHaveLength(2);
      expect(store.revision()).toBe(8);
    });
  });

  it('seeds item status from the live snapshot once the group is joined', async () => {
    live.projects.set({
      Dune: {
        folder: 'Dune',
        revision: 5,
        nodes: {},
        folderAudioRemaining: 0,
        paragraphs: { p9: { status: 'Queued' } },
        items: { i9: { status: 'Processing' } },
      },
    });
    await open();
    await settle();
    expect(store.paragraphs()['p9']).toEqual({ status: 'Queued' });
    expect(store.audioInFlight()).toBe(1);
  });

  describe('viewing a History step (ticket 32)', () => {
    it('is read-only with the viewing reason, and not on the current project', async () => {
      await open();
      expect(store.readOnly()).toBeNull();
      use(StepView).view('dune', 's8');
      expect(store.readOnly()).toBe(VIEWING_READ_ONLY);
      use(StepView).view('emma', 's8');
      expect(store.readOnly()).toBeNull();
    });

    it('drops status deltas while viewing, and applies them again after Back to current', async () => {
      await open();
      use(StepView).view('dune', 's8');

      live.nodeStatus.emit({
        folder: 'dune',
        nodes: { v1: null },
        folderAudioRemaining: 0,
      });
      live.itemStatus.emit({
        folder: 'dune',
        paragraphs: {},
        items: { i1: { status: 'Queued' } },
      });
      expect(store.nodes()['v1']).toBeDefined();
      expect(store.folderAudioRemaining()).toBe(2);
      expect(store.audioInFlight()).toBe(0);

      use(StepView).clear('dune');
      live.nodeStatus.emit({
        folder: 'dune',
        nodes: { v1: null },
        folderAudioRemaining: 0,
      });
      live.itemStatus.emit({
        folder: 'dune',
        paragraphs: {},
        items: { i1: { status: 'Queued' } },
      });
      expect(store.nodes()['v1']).toBeUndefined();
      expect(store.folderAudioRemaining()).toBe(0);
      expect(store.audioInFlight()).toBe(1);
    });

    it('back to current, the reload takes the item status the live snapshot moved to meanwhile', async () => {
      await open();
      use(StepView).view('dune', 's8');
      live.projects.set({
        dune: {
          folder: 'dune',
          revision: 6,
          nodes: {},
          folderAudioRemaining: 0,
          paragraphs: {},
          items: { i4: { status: 'Processing' } },
        },
      });

      use(StepView).clear('dune');
      await store.reload();
      expect(store.audioInFlight()).toBe(1);
    });
  });

  it('close leaves the group and stops listening', async () => {
    await open();
    store.close();
    expect(live.left).toEqual(['dune']);
    live.nodeStatus.emit({ folder: 'dune', nodes: { v1: null }, folderAudioRemaining: 0 });
    expect(store.nodes()['v1']).toBeDefined();
  });

  describe('pipeline (guidance-primitives 16)', () => {
    it('is null until /status answers, then the nine steps derived from the store', async () => {
      expect(store.pipeline()).toBeNull();
      await open();
      expect(store.pipeline()?.map((s) => s.id)).toHaveLength(9);
      expect(store.pipeline()?.find((s) => s.id === 'speakers')?.chip.label).toBe(
        '2 paragraphs unassigned',
      );
    });

    it('reads the newest build on open and shows it on the Export step', async () => {
      await open(statusDto(), [FULL_BUILD]);
      expect(api.calls('GET', OUTPUTS_URL)).toHaveLength(1);
      expect(exportStep()?.state).toBe('done');
      expect(exportStep()?.detail).toBe(`Last build: ${formatBuildDate(FULL_BUILD.createdAt)}`);
    });

    it('a failed outputs read leaves Export saying nothing was built', async () => {
      await open(statusDto(), () => problem(500, 'disk'));
      expect(exportStep()?.chip.label).toBe('Not built');
      expect(store.error()).toBeNull();
    });

    it('reloads the build when this project’s assembly completes, not another’s', async () => {
      await open();
      api.on('GET', OUTPUTS_URL, [FULL_BUILD]);

      live.assemblyMessages.emit({ kind: 'completed', folder: 'emma' });
      await settle();
      expect(api.calls('GET', OUTPUTS_URL)).toHaveLength(1);

      live.assemblyMessages.emit({ kind: 'completed', folder: 'DUNE' });
      await settle();
      expect(api.calls('GET', OUTPUTS_URL)).toHaveLength(2);
      expect(exportStep()?.state).toBe('done');
    });

    it('follows the hub’s assembly and voice-batch running flags', async () => {
      await open();
      live.assembly.set({ ...IDLE_ASSEMBLY, isRunning: true, folder: 'dune' });
      live.voiceBatch.set({ ...IDLE_VOICE_BATCH, isRunning: true });
      expect(exportStep()?.chip.label).toBe('Assembling…');
      expect(store.pipeline()?.find((s) => s.id === 'voices')?.state).toBe('running');
    });

    it('receipt → refetch → re-derive: a confirmed Structure row reaches the step', async () => {
      await open();
      expect(store.pipeline()?.find((s) => s.id === 'structure')?.state).toBe('unconfirmed');
      api.on(
        'GET',
        STATUS_URL,
        statusDto({
          checkpoints: { structure: { state: 'confirmed', stale: false, at: '2026-10-17' } },
        }),
      );

      live.receiptsEmitter.emit(receipt(6, 'Checkpoints'));
      await settle(REFETCH_DEBOUNCE_MS + 20);

      expect(store.pipeline()?.find((s) => s.id === 'structure')?.state).toBe('confirmed');
      expect(store.pipeline()?.find((s) => s.next)?.id).toBe('speakers');
    });
  });

  it('surfaces a load failure as an error', async () => {
    api.on('GET', DETAIL_URL, () => problem(404, 'gone'));
    api.on('GET', STATUS_URL, () => problem(404, 'gone'));
    await store.open('dune');
    expect(store.error()).toBeTruthy();
    expect(store.detail()).toBeNull();
  });
});
