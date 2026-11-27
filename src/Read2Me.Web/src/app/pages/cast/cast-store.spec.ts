import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  StepView,
  VIEWING_READ_ONLY,
  type CharacterSummaryDto,
  type CharacterVoicesDto,
  type VoiceDto,
  type BookFacets,
  type ReceiptMessage,
} from '@app/api';
import { override, resetServices, use } from '@app/core/services';
import type { Receipt } from '@app/live/live-messages';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { projectSnapshot } from '../../../testing/fake-hub-connection';
import { CastStore } from './cast-store';
import { voiceBatchMessage } from '../../../testing/live-messages';

/** The Cast page follows "dracula": the hub has answered for it, so the feed has its baseline. */
const REVISION = 1;

const SUMMARY = '/api/projects/dracula/characters/summary';
const LINES = '/api/projects/dracula/characters/alice/lines';
const VOICES = '/api/projects/dracula/characters/alice/voices';
const RULES = '/api/projects/dracula/characters/alice/voice-rules';
const PREVIEW = '/api/projects/dracula/characters/alice/voice-rules/preview';
const COMMANDS = '/api/projects/dracula/commands';

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
    seriesLink: null,
  };
}

function receipt(facets: BookFacets[]): Receipt {
  const message: ReceiptMessage = {
    folder: 'dracula',
    mutationName: 'X',
    mutationId: 'm',
    revision: REVISION,
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
    originId: 'o',
  };
  return { ...message, isOwn: false };
}

const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

let api: FakeApi;
let live: FakeLive;
let problems: unknown[];
let warnings: string[];
let store: CastStore;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  live.seed('dracula', REVISION);
  problems = [];
  warnings = [];
  override(ToastService, {
    problem: (p: unknown) => problems.push(p),
    warn: (m: string) => warnings.push(m),
  } as unknown as ToastService);
  store = new CastStore();
});
afterEach(() => store.close());

async function open(rows = [row('Narrator'), row('Alice', 2)]) {
  api.on('GET', SUMMARY, rows);
  api.on('GET', VOICES, voiceList([]));
  api.on('GET', RULES, []);
  api.on('GET', PREVIEW, []);
  await store.open('dracula');
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
    await store.open('dracula');
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
      api.on('GET', `/api/projects/dracula/characters/ghost/${read}`, []);
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
    live.emit('receipt', receipt(['Characters']));
    live.emit('receipt', receipt(['Attribution', 'Audio']));
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', LINES)).toHaveLength(2);
    expect(api.calls('GET', VOICES)).toHaveLength(2);
    expect(store.selected()?.lineCount).toBe(3);
    expect(store.lines().map((l) => l.text)).toEqual(['New']);
    expect(store.voices().voices.map((v) => v.name)).toEqual(['New']);
  });

  it('reloads after a hub reconnect whose snapshot is ahead of what it saw, and not otherwise', async () => {
    await open();
    live.resync({ projects: { dracula: projectSnapshot('dracula', REVISION) } } as never);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);

    // A character added in another browser while the hub was away: no receipt reached this tab.
    api.on('GET', SUMMARY, [row('Narrator'), row('Alice', 2), row('Van Helsing')]);
    live.resync({ projects: { dracula: projectSnapshot('dracula', REVISION + 2) } } as never);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(store.rows().map((r) => r.name)).toEqual(['Narrator', 'Alice', 'Van Helsing']);
  });

  it('a batch voiceUpdated patches the selected voice in place and bumps its audio version', async () => {
    await open();
    api.on('GET', LINES, []);
    api.on('GET', VOICES, voiceList([{ id: 'v1' }, { id: 'v2', name: 'Other' }]));
    await store.select('alice');

    live.emit(
      'voiceBatch',
      voiceBatchMessage({
        kind: 'VoiceUpdated',
        characterId: 'alice',
        voiceId: 'v1',
        designPrompt: 'warm alto',
        audioFileName: 'voices/alice/v1.wav',
      }),
    );
    expect(store.voices().voices[0]?.designPrompt).toBe('warm alto');
    expect(store.voices().voices[0]?.audioFileName).toBe('voices/alice/v1.wav');
    expect(store.audioVersions()['v1']).toBe(1);
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', VOICES)).toHaveLength(1);

    // Another character's voice is not this panel's business.
    live.emit(
      'voiceBatch',
      voiceBatchMessage({
        kind: 'VoiceUpdated',
        characterId: 'bob',
        voiceId: 'v9',
        designPrompt: 'x',
      }),
    );
    expect(store.voices().voices.map((v) => v.designPrompt)).toEqual(['warm alto', null]);
  });

  it('a voiceUpdated for a voice this tab has not seen, and a completed batch, reload', async () => {
    await open();
    api.on('GET', LINES, []);
    await store.select('alice');

    api.on('GET', VOICES, voiceList([{ id: 'v-new', designPrompt: 'x' }]));
    live.emit(
      'voiceBatch',
      voiceBatchMessage({
        kind: 'VoiceUpdated',
        characterId: 'alice',
        voiceId: 'v-new',
        designPrompt: 'x',
      }),
    );
    live.emit('voiceBatch', voiceBatchMessage({ kind: 'Completed', processed: 1, failed: 0 }));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(2);
    expect(api.calls('GET', VOICES)).toHaveLength(2);
    expect(store.voices().voices.map((v) => v.id)).toEqual(['v-new']);
  });

  it('a VoiceRules or Structure receipt reloads the selected rules and preview, but only with a selection', async () => {
    await open();
    live.emit('receipt', receipt(['Structure']));
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
    live.emit('receipt', receipt(['VoiceRules']));
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
    live.emit('receipt', receipt(['ItemText', 'NodeTitle']));
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

  describe('a run while another is in flight (#535)', () => {
    /** Holds every command until `release(i)`; answers it with `newEntityId` = its name. */
    function holdCommands() {
      const answers: ((v: unknown) => void)[] = [];
      api.on(
        'POST',
        COMMANDS,
        (body) =>
          new Promise((r) => {
            const name = (body as { name?: string }).name ?? null;
            answers.push(() => r({ outcome: 'Committed', newEntityId: name }));
          }),
      );
      return {
        release: async (i: number) => {
          answers[i]!(undefined);
          await settle();
        },
        count: () => answers.length,
      };
    }

    it('waits, then is sent once the earlier one lands; busy holds until the last', async () => {
      await open();
      const held = holdCommands();
      const first = store.run({ type: 'CreateCharacter', name: 'Bob' });
      const second = store.run({ type: 'RenameCharacter', characterId: 'alice', name: 'Al' });
      await settle();
      expect(held.count()).toBe(1);

      await held.release(0);
      expect((await first)?.newEntityId).toBe('Bob');
      expect(store.busy()).toBe(true);
      // The first reloaded before the second was sent.
      expect(api.calls('GET', SUMMARY)).toHaveLength(2);
      expect(held.count()).toBe(2);

      await held.release(1);
      expect((await second)?.newEntityId).toBe('Al');
      expect(store.busy()).toBe(false);
      expect(api.calls('POST', COMMANDS).map((c) => c.body)).toEqual([
        { type: 'CreateCharacter', name: 'Bob' },
        { type: 'RenameCharacter', characterId: 'alice', name: 'Al' },
      ]);
      expect(api.calls('GET', SUMMARY)).toHaveLength(3);
    });

    it('a refused earlier command does not stop the next', async () => {
      await open();
      let calls = 0;
      api.on('POST', COMMANDS, () =>
        ++calls === 1 ? problem(422, 'Name taken') : { outcome: 'Committed' },
      );
      const first = store.run({ type: 'CreateCharacter', name: 'Bob' });
      const second = store.run({ type: 'CreateCharacter', name: 'Carol' });
      expect(await first).toBeUndefined();
      expect(await second).toBeDefined();
      expect(problems).toHaveLength(1);
      expect(store.busy()).toBe(false);
    });

    it('is not sent when the Edit gate closed before its turn, and says why', async () => {
      await open();
      const held = holdCommands();
      const first = store.run({ type: 'CreateCharacter', name: 'Bob' });
      const second = store.run({ type: 'RenameCharacter', characterId: 'alice', name: 'Al' });
      await settle();
      use(StepView).view('dracula', 's8');
      await held.release(0);
      await first;
      expect(await second).toBeUndefined();
      expect(held.count()).toBe(1);
      expect(warnings).toEqual([`Not saved. ${VIEWING_READ_ONLY}`]);
      expect(store.busy()).toBe(false);
    });

    it('is sent to the folder it was made for, and reloads only if that folder is still open', async () => {
      await open();
      const held = holdCommands();
      const first = store.run({ type: 'CreateCharacter', name: 'Bob' });
      const second = store.run({ type: 'RenameCharacter', characterId: 'alice', name: 'Al' });
      await settle();
      api.on('GET', '/api/projects/emma/characters/summary', [row('Narrator')]);
      await store.open('emma');
      await held.release(0);
      await first;
      await held.release(1);
      await second;
      expect(api.calls('POST', COMMANDS)).toHaveLength(2);
      expect(api.calls('POST', '/api/projects/emma/commands')).toHaveLength(0);
      // Only the open load of emma; no reload of either folder after the switch.
      expect(api.calls('GET', SUMMARY)).toHaveLength(1);
      expect(api.calls('GET', '/api/projects/emma/characters/summary')).toHaveLength(1);
      expect(store.busy()).toBe(false);
    });
  });

  it('close drops the selection and stops listening', async () => {
    await open();
    store.close();
    expect(store.folder()).toBeNull();
    live.emit('receipt', receipt(['Characters']));
    await settle(REFETCH_DEBOUNCE_MS + 20);
    expect(api.calls('GET', SUMMARY)).toHaveLength(1);
  });
});
