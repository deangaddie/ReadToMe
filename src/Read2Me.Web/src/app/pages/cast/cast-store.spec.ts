import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto, CharacterVoicesDto, VoiceDto } from '@app/api';
import { Emitter } from '@app/core/emitter';
import { override, resetServices } from '@app/core/services';
import type { Receipt, ReceiptMessage, VoiceBatchMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { CastStore } from './cast-store';

/** The slice of LiveService the store uses: project receipts and voice-batch messages over Emitters. */
class FakeLive {
  readonly receiptsEmitter = new Emitter<Receipt>();
  readonly voiceBatchEmitter = new Emitter<VoiceBatchMessage>();
  receipts(_folder: string, listener: (r: Receipt) => void) {
    return this.receiptsEmitter.subscribe(listener);
  }
  on(family: string, listener: (m: VoiceBatchMessage) => void) {
    return family === 'voiceBatch' ? this.voiceBatchEmitter.subscribe(listener) : () => undefined;
  }
}

const SUMMARY = '/api/projects/dune/characters/summary';
const LINES = '/api/projects/dune/characters/alice/lines';
const VOICES = '/api/projects/dune/characters/alice/voices';
const RULES = '/api/projects/dune/characters/alice/voice-rules';
const PREVIEW = '/api/projects/dune/characters/alice/voice-rules/preview';
const COMMANDS = '/api/projects/dune/commands';

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
      referenceSeconds: null,
      referenceWarning: null,
      ...v,
    })),
  };
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
  api.on('GET', VOICES, voiceList([]));
  api.on('GET', RULES, []);
  api.on('GET', PREVIEW, []);
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

  it('selecting a character loads its lines and voices; the selection follows the roster', async () => {
    await open();
    api.on('GET', LINES, [{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'Hi' }]);
    api.on('GET', VOICES, voiceList([{ name: 'Main' }]));
    await store.select('alice');
    expect(store.selected()?.name).toBe('Alice');
    expect(store.lines().map((l) => l.text)).toEqual(['Hi']);
    expect(store.voices().voices.map((v) => v.name)).toEqual(['Main']);
    expect(store.linesLoading()).toBe(false);
    expect(store.voicesLoading()).toBe(false);

    await store.select(null);
    expect(store.selected()).toBeNull();
    expect(store.lines()).toEqual([]);
    expect(store.voices().voices).toEqual([]);
  });

  it('an unknown selection is null, not an error', async () => {
    await open();
    for (const read of ['lines', 'voices', 'voice-rules', 'voice-rules/preview']) {
      api.on('GET', `/api/projects/dune/characters/ghost/${read}`, []);
    }
    await store.select('ghost');
    expect(store.selectedId()).toBe('ghost');
    expect(store.selected()).toBeNull();
  });

  it('a cast-facet receipt reloads the roster and the selected lines and voices once per burst', async () => {
    await open();
    api.on('GET', LINES, []);
    await store.select('alice');

    api.on('GET', SUMMARY, [row('Narrator'), row('Alice', 3)]);
    api.on('GET', LINES, [{ itemId: 'i1', paragraphId: 'p1', chapterId: 'c1', text: 'New' }]);
    api.on('GET', VOICES, voiceList([{ name: 'New' }]));
    live.receiptsEmitter.emit(receipt('Characters'));
    live.receiptsEmitter.emit(receipt('Attribution, Audio'));
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', LINES)).toHaveLength(2);
    expect(api.calls('GET', VOICES)).toHaveLength(2);
    expect(store.selected()?.lineCount).toBe(3);
    expect(store.lines().map((l) => l.text)).toEqual(['New']);
    expect(store.voices().voices.map((v) => v.name)).toEqual(['New']);
  });

  it('a batch voiceUpdated patches the selected voice in place and bumps its audio version', async () => {
    await open();
    api.on('GET', LINES, []);
    api.on('GET', VOICES, voiceList([{ id: 'v1' }, { id: 'v2', name: 'Other' }]));
    await store.select('alice');

    live.voiceBatchEmitter.emit({
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
    expect(api.calls('GET', VOICES)).toHaveLength(1);

    // Another character's voice is not this panel's business.
    live.voiceBatchEmitter.emit({
      kind: 'voiceUpdated',
      characterId: 'bob',
      voiceId: 'v9',
      designPrompt: 'x',
    });
    expect(store.voices().voices.map((v) => v.designPrompt)).toEqual(['warm alto', null]);
  });

  it('a voiceUpdated for a voice this tab has not seen, and a completed batch, reload', async () => {
    await open();
    api.on('GET', LINES, []);
    await store.select('alice');

    api.on('GET', VOICES, voiceList([{ id: 'v-new', designPrompt: 'x' }]));
    live.voiceBatchEmitter.emit({
      kind: 'voiceUpdated',
      characterId: 'alice',
      voiceId: 'v-new',
      designPrompt: 'x',
    });
    live.voiceBatchEmitter.emit({ kind: 'completed', processed: 1, failed: 0 });
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', VOICES)).toHaveLength(2);
    expect(store.voices().voices.map((v) => v.id)).toEqual(['v-new']);
  });

  it('a VoiceRules or Structure receipt reloads the selected rules and preview, but only with a selection', async () => {
    await open();
    live.receiptsEmitter.emit(receipt('Structure'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);

    api.on('GET', LINES, []);
    await store.select('alice');
    expect(store.voiceRules()).toEqual([]);

    api.on('GET', RULES, [
      {
        ruleId: 'r1',
        voiceId: 'v1',
        voiceName: 'Main',
        isDefault: true,
        fromLevel: null,
        fromNodeId: null,
        fromTitle: null,
        fromDangling: false,
        toLevel: null,
        toNodeId: null,
        toTitle: null,
        toDangling: false,
        order: 'a0',
      },
    ]);
    api.on('GET', PREVIEW, [{ chapterId: 'c1', chapterTitle: 'One', voiceName: 'Main' }]);
    live.receiptsEmitter.emit(receipt('VoiceRules'));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', RULES)).toHaveLength(2);
    expect(store.voiceRules().map((r) => r.voiceName)).toEqual(['Main']);
    expect(store.voiceRulePreview().map((p) => p.voiceName)).toEqual(['Main']);
    expect(store.voiceRulesLoading()).toBe(false);
  });

  it('refreshVoices reloads only the selected voices; roster and anyVoices read the rows', async () => {
    await open([row('Narrator'), { ...row('Alice', 2), voiceCount: 1 }]);
    expect(store.anyVoices()).toBe(true);
    expect(store.roster()).toEqual([
      { id: 'narrator', name: 'Narrator', aliases: [] },
      { id: 'alice', name: 'Alice', aliases: [] },
    ]);
    api.on('GET', LINES, []);
    await store.select('alice');
    api.on('GET', VOICES, voiceList([{ name: 'Fresh' }]));
    await store.refreshVoices();
    expect(store.voices().voices.map((v) => v.name)).toEqual(['Fresh']);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
    expect(api.calls('GET', LINES)).toHaveLength(1);
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
