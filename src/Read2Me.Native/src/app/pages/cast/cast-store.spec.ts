import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import type { Receipt, ReceiptMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { CastStore } from './cast-store';

/** The slice of LiveService the store uses: project receipts over an Emitter. */
class FakeLive {
  readonly receiptsEmitter = new Emitter<Receipt>();
  receipts(_folder: string, listener: (r: Receipt) => void) {
    return this.receiptsEmitter.subscribe(listener);
  }
  on() {
    return () => undefined;
  }
}

const SUMMARY = '/api/projects/dune/characters/summary';
const LINES = '/api/projects/dune/characters/alice/lines';
const COMMANDS = '/api/projects/dune/commands';

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

const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

let api: FakeApi;
let live: FakeLive;
let problems: unknown[];
let store: CastStore;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive();
  override(LiveService, live as unknown as LiveService);
  problems = [];
  override(ToastService, { problem: (p: unknown) => problems.push(p) } as unknown as ToastService);
  store = new CastStore();
});
afterEach(() => store.close());

async function open(rows = [row('Narrator'), row('Alice', 2)]) {
  api.on('GET', SUMMARY, rows);
  await store.open('dune');
}

describe('CastStore', () => {
  it('loads the roster', async () => {
    await open();
    expect(store.rows().map((r) => r.name)).toEqual(['Narrator', 'Alice']);
    expect(store.loading()).toBe(false);
    expect(store.selected()).toBeNull();
  });

  it('a failed load keeps the error', async () => {
    api.on('GET', SUMMARY, () => problem(500, 'boom'));
    await store.open('dune');
    expect(store.error()).toBe('boom');
    expect(store.loading()).toBe(false);
  });

  it('selecting a character loads its lines; the selection follows the roster', async () => {
    await open();
    api.on('GET', LINES, [{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'Hi' }]);
    await store.select('alice');
    expect(store.selected()?.name).toBe('Alice');
    expect(store.lines().map((l) => l.text)).toEqual(['Hi']);
    expect(store.linesLoading()).toBe(false);

    await store.select(null);
    expect(store.selected()).toBeNull();
    expect(store.lines()).toEqual([]);
  });

  it('an unknown selection is null, not an error', async () => {
    await open();
    api.on('GET', '/api/projects/dune/characters/ghost/lines', []);
    await store.select('ghost');
    expect(store.selectedId()).toBe('ghost');
    expect(store.selected()).toBeNull();
  });

  it('a cast-facet receipt reloads the roster and the selected lines once per burst', async () => {
    await open();
    api.on('GET', LINES, []);
    await store.select('alice');

    api.on('GET', SUMMARY, [row('Narrator'), row('Alice', 3)]);
    api.on('GET', LINES, [{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'New' }]);
    live.receiptsEmitter.emit(receipt('Characters'));
    live.receiptsEmitter.emit(receipt('Attribution, Audio'));
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', LINES)).toHaveLength(2);
    expect(store.selected()?.lineCount).toBe(3);
    expect(store.lines().map((l) => l.text)).toEqual(['New']);
  });

  it('ignores receipts that touch nothing the roster shows', async () => {
    await open();
    live.receiptsEmitter.emit(receipt('ItemText, NodeTitle'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
  });

  it('run posts the command, then reloads; the response is answered', async () => {
    await open();
    api.on('POST', COMMANDS, { outcome: 'Committed', newEntityId: 'bob' });
    api.on('GET', SUMMARY, [row('Narrator'), row('Alice', 2), row('Bob')]);
    const running = store.run({ type: 'CreateCharacter', name: 'Bob' });
    expect(store.busy()).toBe(true);
    const response = await running;
    expect(response?.newEntityId).toBe('bob');
    expect(store.busy()).toBe(false);
    expect(store.rows().map((r) => r.name)).toContain('Bob');
  });

  it('a refused command is toasted and nothing reloads', async () => {
    await open();
    api.on('POST', COMMANDS, () => problem(422, 'Name is required.'));
    expect(
      await store.run({ type: 'RenameCharacter', characterId: 'alice', name: '' }),
    ).toBeUndefined();
    expect(problems).toHaveLength(1);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
  });

  it('a second run while one is in flight is refused', async () => {
    await open();
    let answer!: (v: unknown) => void;
    api.on('POST', COMMANDS, () => new Promise((r) => (answer = r)));
    api.on('GET', SUMMARY, [row('Narrator')]);
    const first = store.run({ type: 'CreateCharacter', name: 'Bob' });
    expect(await store.run({ type: 'CreateCharacter', name: 'Carol' })).toBeUndefined();
    answer({ outcome: 'Committed' });
    await first;
    expect(api.calls('POST', COMMANDS)).toHaveLength(1);
  });

  it('close drops the selection and stops listening', async () => {
    await open();
    store.close();
    expect(store.folder()).toBeNull();
    live.receiptsEmitter.emit(receipt('Characters'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
  });
});
