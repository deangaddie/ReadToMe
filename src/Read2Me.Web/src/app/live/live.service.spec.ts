import type { LiveSnapshot, ReceiptMessage } from '@app/api';
import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import { ORIGIN_ID } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { effect } from '@app/core/signals';
import { ToastService } from '@app/ui/toast';
import { FakeTimers, settleMicrotasks as settle } from '../../testing/fake-timers';
import {
  LIVE_CONNECTION_FACTORY,
  type LiveConnectionOptions,
  RECONNECT_DELAYS_MS,
  reconnectDelayMs,
} from './live-connection';
import type { Receipt } from './live-messages';
import { DISCONNECT_TOAST_AFTER_MS, LiveService } from './live.service';
import {
  assemblyMessage,
  itemEntry,
  paragraphEntry,
  serviceStatusMessage,
  voiceBatchMessage,
} from '../../testing/live-messages';
import { FakeConnection, hubSnapshot, projectSnapshot } from '../../testing/fake-hub-connection';

function receipt(folder: string, originId: string): ReceiptMessage {
  return {
    folder,
    mutationName: 'CreateCharacterMutation',
    mutationId: 'm1',
    revision: 7,
    effects: {
      scope: 'Exact',
      createdId: null,
      createdParentId: null,
      facets: ['Characters'],
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
    originId,
  };
}

// ---- tests ---------------------------------------------------------------------------------------

describe('reconnectDelayMs', () => {
  it('walks 0, 2 s, 5 s, 10 s, then stays at 30 s forever', () => {
    expect([0, 1, 2, 3, 4, 5, 50].map(reconnectDelayMs)).toEqual([
      0, 2000, 5000, 10000, 30000, 30000, 30000,
    ]);
    expect(RECONNECT_DELAYS_MS).toEqual([0, 2000, 5000, 10000, 30000]);
  });
});

describe('LiveService', () => {
  let timers: FakeTimers;
  let connections: FakeConnection[];
  /** Applied to the next connection the factory builds. */
  let startFailures = 0;
  let toast: { warn: ReturnType<typeof mock>; success: ReturnType<typeof mock> };
  let service: LiveService;

  const current = () => connections[connections.length - 1]!;

  beforeEach(() => {
    resetServices();
    timers = new FakeTimers().install();
    connections = [];
    startFailures = 0;
    toast = { warn: mock(), success: mock() };
    override(LIVE_CONNECTION_FACTORY, (options: LiveConnectionOptions) => {
      const c = new FakeConnection(options);
      c.startFailures = startFailures;
      c.results.set('GetSnapshot', () => hubSnapshot());
      c.results.set('JoinProject', (folder) => projectSnapshot(String(folder)));
      connections.push(c);
      return c;
    });
    override(ToastService, toast as unknown as ToastService);
    service = new LiveService();
  });

  afterEach(async () => {
    await service.stop();
    timers.restore();
  });

  it('starts disconnected, connects, and applies the snapshot', async () => {
    expect(service.state()).toBe('disconnected');
    service.start();
    expect(service.state()).toBe('connecting');
    await settle();

    expect(service.state()).toBe('connected');
    expect(service.statusLabel()).toBe('Live updates connected');
    expect(current().calls('GetSnapshot')).toHaveLength(1);
    expect(service.queue()?.attribution.queuedCount).toBe(3);
    expect(service.attributionProgress()).toEqual({
      step: 2,
      configName: 'Qwen 28B',
      itemCount: 3,
    });
    expect(service.watchdog()).toEqual({ llama: 'ServiceHealthy' });
    expect(service.serviceStatus()).toEqual({ llama: 'Ready' });
    expect(service.assembly().audioRemainingCount).toBe(4);
  });

  it('registers a handler for every hub family', async () => {
    service.start();
    await settle();
    expect(Array.from(current().handlers.keys()).sort()).toEqual(
      [
        'assembly',
        'audioGen',
        'bookEdit',
        'history',
        'itemStatus',
        'llm',
        'llmTest',
        'nodeStatus',
        'preflight',
        'queue',
        'receipt',
        'series',
        'serviceStatus',
        'settingsChanged',
        'setupCheck',
        'throughput',
        'voiceBatch',
        'watchdog',
      ].sort(),
    );
  });

  it('answers the connection id only while connected', async () => {
    expect(service.connectionId()).toBeNull();

    service.start();
    await settle();
    expect(service.connectionId()).toBe('conn-1');

    // A reconnect mints a new id, so callers must read it per request rather than hold it.
    current().connectionId = 'conn-2';
    expect(service.connectionId()).toBe('conn-2');
  });

  it('retries a failed initial start with the backoff and reports disconnected meanwhile', async () => {
    startFailures = 2;
    service.start();
    await settle();
    expect(current().startCalls).toBe(1);
    expect(service.state()).toBe('disconnected');
    expect(service.statusLabel()).toBe('Disconnected — retrying');

    await timers.advance(reconnectDelayMs(0));
    await settle();
    expect(current().startCalls).toBe(2);
    expect(service.state()).toBe('disconnected');

    await timers.advance(reconnectDelayMs(1));
    await settle();
    expect(current().startCalls).toBe(3);
    expect(service.state()).toBe('connected');
    expect(current().calls('GetSnapshot')).toHaveLength(1);
  });

  it('restarts the connect loop when the connection closes for good', async () => {
    service.start();
    await settle();
    current().close();
    expect(service.state()).toBe('disconnected');
    await settle();
    expect(current().startCalls).toBe(2);
    expect(service.state()).toBe('connected');
    expect(current().calls('GetSnapshot')).toHaveLength(2);
  });

  it('re-joins remembered groups and requests a fresh snapshot on reconnect', async () => {
    service.start();
    await settle();
    await service.joinProject('pride-and-prejudice');
    await service.joinStream('llm');
    const resynced: LiveSnapshot[] = [];
    service.resynced((s) => resynced.push(s));
    expect(current().calls('JoinProject')).toEqual([['pride-and-prejudice']]);
    expect(current().calls('JoinStream')).toEqual([['llm']]);
    expect(current().calls('GetSnapshot')).toHaveLength(1);

    current().drop();
    expect(service.state()).toBe('reconnecting');
    expect(service.statusLabel()).toBe('Reconnecting… attempt 1');
    current().retry(3);
    expect(service.statusLabel()).toBe('Reconnecting… attempt 3');

    current().results.set('GetSnapshot', () =>
      hubSnapshot({ 'pride-and-prejudice': projectSnapshot('pride-and-prejudice', 9) }),
    );
    current().recover();
    await settle();

    expect(service.state()).toBe('connected');
    expect(service.attempt()).toBe(0);
    expect(current().calls('JoinProject')).toEqual([
      ['pride-and-prejudice'],
      ['pride-and-prejudice'],
    ]);
    expect(current().calls('JoinStream')).toEqual([['llm'], ['llm']]);
    expect(current().calls('GetSnapshot')).toHaveLength(2);
    expect(resynced).toHaveLength(1);
    expect(service.projects()['pride-and-prejudice']?.revision).toBe(9);
  });

  it('marks a joined project seeded once the hub answers for it: JoinProject, then a snapshot', async () => {
    await service.joinProject('pride-and-prejudice');
    expect(service.projects()['pride-and-prejudice']?.seeded).toBe(false);

    let answer: (snapshot: unknown) => void = () => {};
    service.start();
    current().results.set('JoinProject', () => new Promise((resolve) => (answer = resolve)));
    await settle();
    expect(service.projects()['pride-and-prejudice']?.seeded).toBe(false);

    answer(projectSnapshot('pride-and-prejudice', 4));
    await settle();
    expect(service.projects()['pride-and-prejudice']).toMatchObject({ revision: 4, seeded: true });

    current().results.set('JoinProject', () => undefined);
    current().results.set('GetSnapshot', () =>
      hubSnapshot({ 'pride-and-prejudice': projectSnapshot('pride-and-prejudice', 6) }),
    );
    current().drop();
    current().recover();
    await settle();
    expect(service.projects()['pride-and-prejudice']).toMatchObject({ revision: 6, seeded: true });
  });

  it('reference-counts project membership and joins only while connected', async () => {
    await service.joinProject('pride-and-prejudice'); // before start: remembered, not invoked
    service.start();
    await settle();
    expect(current().calls('JoinProject')).toEqual([['pride-and-prejudice']]);

    await service.joinProject('Pride-and-Prejudice'); // case-insensitive second reference
    await service.joinProject('pride-and-prejudice');
    expect(current().calls('JoinProject')).toHaveLength(1);

    await service.leaveProject('pride-and-prejudice');
    await service.leaveProject('PRIDE-AND-PREJUDICE');
    expect(current().calls('LeaveProject')).toHaveLength(0);
    await service.leaveProject('pride-and-prejudice');
    expect(current().calls('LeaveProject')).toEqual([['pride-and-prejudice']]);
    expect(service.joinedProjects()).toEqual([]);
    expect(service.projects()['pride-and-prejudice']).toBeUndefined();

    await service.leaveProject('pride-and-prejudice'); // extra leave is a no-op
    expect(current().calls('LeaveProject')).toHaveLength(1);
  });

  it('reference-counts stream membership', async () => {
    service.start();
    await settle();
    await service.joinStream('llm');
    await service.joinStream('llm');
    await service.joinStream('audio');
    expect(current().calls('JoinStream')).toEqual([['llm'], ['audio']]);

    await service.leaveStream('llm');
    expect(current().calls('LeaveStream')).toHaveLength(0);
    await service.leaveStream('llm');
    await service.leaveStream('audio');
    expect(current().calls('LeaveStream')).toEqual([['llm'], ['audio']]);
    expect(service.joinedStreams()).toEqual([]);
  });

  it('filters receipts by folder and flags this tab’s own writes', async () => {
    service.start();
    await settle();
    const receipts: Receipt[] = [];
    service.receipts('pride-and-prejudice', (r) => receipts.push(r));

    current().emit('receipt', receipt('other', ORIGIN_ID));
    current().emit('receipt', receipt('Pride-and-Prejudice', ORIGIN_ID));
    current().emit('receipt', receipt('pride-and-prejudice', 'someone-else'));

    expect(receipts.map((r) => [r.folder, r.isOwn])).toEqual([
      ['Pride-and-Prejudice', true],
      ['pride-and-prejudice', false],
    ]);
  });

  it('folds messages into the snapshot-shaped signals and the typed streams', async () => {
    service.start();
    await settle();
    await service.joinProject('pride-and-prejudice');
    const assemblies: string[] = [];
    service.on('assembly', (m) => assemblies.push(m.kind));

    current().emit('assembly', assemblyMessage({ kind: 'PhaseStarted', phase: 'Encode' }));
    current().emit('assembly', assemblyMessage({ kind: 'Progress', fraction: 0.42 }));
    expect(service.assembly()).toMatchObject({
      isRunning: true,
      currentPhase: 'Encode',
      encodePercent: 42,
    });
    current().emit('assembly', assemblyMessage({ kind: 'Failed', reason: 'ffmpeg exited 1' }));
    expect(service.assembly()).toMatchObject({ isRunning: false, lastError: 'ffmpeg exited 1' });
    expect(assemblies).toEqual(['PhaseStarted', 'Progress', 'Failed']);

    current().emit(
      'voiceBatch',
      voiceBatchMessage({ kind: 'Started', operation: 'prompts', total: 5 }),
    );
    current().emit(
      'voiceBatch',
      voiceBatchMessage({
        kind: 'Progress',
        processed: 2,
        total: 5,
        failed: 1,
        currentVoiceName: 'Elizabeth',
      }),
    );
    expect(service.voiceBatch()).toMatchObject({
      isRunning: true,
      processed: 2,
      failed: 1,
      currentVoiceName: 'Elizabeth',
      currentOperation: 'prompts',
    });

    current().emit('watchdog', { kind: 'ServiceDown', service: 'llama', reason: 'timeout' });
    expect(service.watchdog()['llama']).toBe('ServiceDown');
    current().emit(
      'serviceStatus',
      serviceStatusMessage({
        name: 'whisper',
        status: 'Stopped',
        op: 'Shutdown',
        ok: true,
      }),
    );
    expect(service.serviceStatus()).toEqual({ llama: 'Ready', whisper: 'Stopped' });

    current().emit('nodeStatus', {
      folder: 'PRIDE-AND-PREJUDICE',
      nodes: {
        n1: {
          attributionRemaining: 2,
          audioRemaining: 0,
          review: 0,
          attributionProcessing: true,
          attributionQueued: 1,
          isDone: false,
        },
      },
      folderAudioRemaining: 12,
    });
    current().emit('nodeStatus', {
      folder: 'pride-and-prejudice',
      nodes: { n1: null },
      folderAudioRemaining: 11,
    });
    expect(service.projects()['pride-and-prejudice']).toMatchObject({
      nodes: {},
      folderAudioRemaining: 11,
    });

    current().emit('itemStatus', {
      folder: 'pride-and-prejudice',
      paragraphs: { p1: paragraphEntry({ status: 'Queued' }) },
      items: { i1: itemEntry({ status: 'Processing', audioVersion: 3 }) },
    });
    expect(service.projects()['pride-and-prejudice']?.paragraphs['p1']).toEqual(
      paragraphEntry({ status: 'Queued' }),
    );
    expect(service.projects()['pride-and-prejudice']?.items['i1']?.audioVersion).toBe(3);

    current().emit('receipt', receipt('pride-and-prejudice', 'x'));
    expect(service.projects()['pride-and-prejudice']?.revision).toBe(7);
    expect(service.latest()['receipt']).toMatchObject({ mutationName: 'CreateCharacterMutation' });
  });

  it('ignores status deltas for projects it has not joined', async () => {
    service.start();
    await settle();
    current().emit('nodeStatus', { folder: 'stranger', nodes: {}, folderAudioRemaining: 1 });
    expect(service.projects()).toEqual({});
  });

  it('toasts once after 5 s disconnected and again when live updates return', async () => {
    service.start();
    await settle();
    current().drop();
    await timers.advance(DISCONNECT_TOAST_AFTER_MS - 1);
    expect(toast.warn).not.toHaveBeenCalled();
    await timers.advance(1);
    expect(toast.warn).toHaveBeenCalledWith('Live updates disconnected — retrying');

    current().recover();
    await settle();
    expect(toast.success).toHaveBeenCalledWith('Live updates reconnected');
    expect(toast.warn).toHaveBeenCalledTimes(1);
  });

  it('does not toast when the connection comes back within 5 s', async () => {
    service.start();
    await settle();
    current().drop();
    await timers.advance(2000);
    current().recover();
    await settle();
    await timers.advance(DISCONNECT_TOAST_AFTER_MS);
    expect(toast.warn).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('stop() ends the loop and reports disconnected without retrying', async () => {
    service.start();
    await settle();
    await service.stop();
    expect(service.state()).toBe('disconnected');
    const calls = current().startCalls;
    await timers.advance(60000);
    expect(current().startCalls).toBe(calls);
    expect(connections).toHaveLength(1);
  });

  it('does not re-run a joining effect when the connection state changes', async () => {
    let runs = 0;
    const dispose = effect((onCleanup) => {
      runs++;
      void service.joinProject('pride-and-prejudice');
      onCleanup(() => void service.leaveProject('pride-and-prejudice'));
    });
    service.start();
    await settle();
    const c = connections[0]!;
    c.drop();
    c.recover();
    await settle();

    expect(runs).toBe(1);
    expect(c.calls('LeaveProject')).toEqual([]);
    expect(c.calls('JoinProject')).toEqual([['pride-and-prejudice'], ['pride-and-prejudice']]);
    dispose();
  });
});
