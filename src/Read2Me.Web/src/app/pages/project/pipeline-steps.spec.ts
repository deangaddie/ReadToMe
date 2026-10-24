import { describe, expect, it } from 'bun:test';
import type { ProjectStatusDto, StepCheckpointDto } from '@app/api';
import type { NodeStatusSummary } from '@app/live/live-messages';
import {
  type PipelineInput,
  type PipelineStep,
  type StepActionId,
  type StepId,
  adviceFor,
  derivePipeline,
  readOnlyPipeline,
} from './pipeline-steps';

const VOL = 'v1';

function node(overrides: Partial<NodeStatusSummary> = {}): NodeStatusSummary {
  const n = {
    attributionRemaining: 0,
    audioRemaining: 0,
    review: 0,
    attributionProcessing: false,
    attributionQueued: 0,
    ...overrides,
  };
  return { ...n, isDone: n.attributionRemaining + n.audioRemaining + n.review === 0 };
}

const AT = '2026-10-17T09:00:00Z';
const confirmed: StepCheckpointDto = { state: 'confirmed', stale: false, at: AT };
const skipped: StepCheckpointDto = { state: 'skipped', stale: false, at: AT };
const staleConfirmed: StepCheckpointDto = { state: 'confirmed', stale: true, at: AT };
const staleSkipped: StepCheckpointDto = { state: 'skipped', stale: true, at: AT };

/** Everything derived holds; only the checkpoint rows are open. */
function status(overrides: Partial<ProjectStatusDto> = {}): ProjectStatusDto {
  return {
    hasContent: true,
    characters: 3,
    charactersWithLines: 2,
    readyVoices: 2,
    items: { total: 10, withAudio: 10, unattributed: 0 },
    attribution: { remaining: 0, processing: false, queued: 0 },
    audio: { remaining: 0 },
    review: 0,
    volumeIds: [VOL],
    nodes: { [VOL]: node() },
    pauseParagraphs: 3,
    checkpoints: {},
    revision: 1,
    ...overrides,
  };
}

const FRESH = status({
  hasContent: false,
  characters: 0,
  charactersWithLines: 0,
  readyVoices: 0,
  items: { total: 0, withAudio: 0, unattributed: 0 },
  volumeIds: [],
  nodes: {},
  pauseParagraphs: 0,
});

const FULL_BUILD = { date: '19 Sep 2026', isPartial: false };

/** Every step settled: rows on the four conditional steps plus the Speakers review, a full build. */
const SETTLED = status({
  checkpoints: { structure: confirmed, speakers: confirmed, text: skipped, titles: confirmed },
});

function input(s: ProjectStatusDto, overrides: Partial<PipelineInput> = {}): PipelineInput {
  return {
    status: s,
    nodes: s.nodes,
    folderAudioRemaining: s.audio.remaining,
    audioInFlight: 0,
    voiceBatchRunning: false,
    assemblyRunning: false,
    lastBuild: null,
    ...overrides,
  };
}

const settled = (overrides: Partial<PipelineInput> = {}) =>
  input(SETTLED, { lastBuild: FULL_BUILD, ...overrides });

const step = (i: PipelineInput, id: StepId): PipelineStep =>
  derivePipeline(i).find((s) => s.id === id)!;

const chip = (i: PipelineInput, id: StepId) => {
  const s = step(i, id);
  return `${s.state} ${s.chip.status}:${s.chip.label}`;
};

const nextOf = (i: PipelineInput) => derivePipeline(i).find((s) => s.next)?.id ?? null;

const pills = (i: PipelineInput, id: StepId) => step(i, id).checkpoint.map((p) => p.id);

const action = (i: PipelineInput, id: StepId, actionId: StepActionId) =>
  step(i, id).actions.find((a) => a.id === actionId)!;

describe('derivePipeline', () => {
  it('lists the nine steps in Pipeline order', () => {
    expect(derivePipeline(input(FRESH)).map((s) => s.id)).toEqual([
      'import',
      'structure',
      'speakers',
      'text',
      'voices',
      'titles',
      'pauses',
      'audio',
      'export',
    ]);
  });

  describe('step state and chip', () => {
    const cases: {
      name: string;
      input: PipelineInput;
      expected: Partial<Record<StepId, string>>;
    }[] = [
      {
        name: 'fresh project: nothing started',
        input: input(FRESH),
        expected: {
          import: 'notStarted neutral:Not started',
          structure: 'notStarted neutral:Not started',
          speakers: 'notStarted neutral:Not started',
          text: 'notStarted neutral:Not started',
          voices: 'notStarted neutral:Not started',
          titles: 'notStarted neutral:Not started',
          pauses: 'notStarted neutral:Not started',
          audio: 'notStarted neutral:Not started',
          export: 'notStarted neutral:Not built',
        },
      },
      {
        name: 'read in, nothing else done: the conditional steps wait for a row',
        input: input(
          status({
            characters: 0,
            charactersWithLines: 1,
            readyVoices: 0,
            items: { total: 10, withAudio: 0, unattributed: 4 },
            audio: { remaining: 6 },
            nodes: { [VOL]: node({ attributionRemaining: 3, audioRemaining: 6 }) },
            pauseParagraphs: 0,
          }),
        ),
        expected: {
          import: 'done ok:Done',
          structure: 'unconfirmed neutral:Not confirmed',
          speakers: 'remaining warn:3 paragraphs unassigned',
          text: 'unconfirmed neutral:Not confirmed',
          voices: 'remaining warn:0 of 1 speakers voiced',
          titles: 'unconfirmed neutral:Not confirmed',
          pauses: 'remaining warn:No pauses yet',
          audio: 'remaining warn:6 paragraphs missing audio',
          export: 'notStarted neutral:Not built',
        },
      },
      {
        name: 'every derived condition holds and every row is in: all settled',
        input: settled(),
        expected: {
          structure: 'confirmed ok:Confirmed',
          speakers: 'confirmed ok:Review confirmed',
          text: 'skipped neutral:Skipped',
          voices: 'done ok:Done',
          titles: 'confirmed ok:Confirmed',
          pauses: 'done ok:Done',
          audio: 'done ok:Done',
          export: 'done ok:Done',
        },
      },
      {
        name: 'speakers unassigned at 0 but no review row',
        input: input(status()),
        expected: { speakers: 'unconfirmed neutral:Review not confirmed' },
      },
      {
        name: 'attribution in flight shows live queued counts',
        input: input(
          status({
            nodes: {
              [VOL]: node({
                attributionRemaining: 5,
                attributionQueued: 4,
                attributionProcessing: true,
              }),
            },
          }),
        ),
        expected: { speakers: 'running busy:4 queued · processing' },
      },
      {
        name: 'attribution queued but not yet processing',
        input: input(
          status({
            nodes: { [VOL]: node({ attributionRemaining: 5, attributionQueued: 5 }) },
          }),
        ),
        expected: { speakers: 'running busy:5 queued' },
      },
      {
        name: 'live node deltas win over the bootstrap attribution count',
        input: input(status({ attribution: { remaining: 9, processing: false, queued: 0 } }), {
          nodes: { [VOL]: node({ attributionRemaining: 1 }) },
        }),
        expected: { speakers: 'remaining warn:1 paragraph unassigned' },
      },
      {
        name: 'live folder audio remaining wins over the bootstrap',
        input: input(status({ audio: { remaining: 5 } }), { folderAudioRemaining: 0 }),
        expected: { audio: 'done ok:Done' },
      },
      {
        name: 'audio items in flight, voice batch and assembly running',
        input: input(status({ audio: { remaining: 3 } }), {
          audioInFlight: 7,
          voiceBatchRunning: true,
          assemblyRunning: true,
        }),
        expected: {
          audio: 'running busy:7 in flight',
          voices: 'running busy:Running',
          export: 'running busy:Assembling…',
        },
      },
      {
        name: 'a read-in book with no audio items has not started Audio',
        input: input(status({ items: { total: 0, withAudio: 0, unattributed: 0 } })),
        expected: { audio: 'notStarted neutral:Not started' },
      },
      {
        name: 'a partial build is not a build',
        input: input(status(), { lastBuild: { date: '19 Sep 2026', isPartial: true } }),
        expected: { export: 'notStarted neutral:Partial build only' },
      },
      {
        name: 'stale rows say what happened since',
        input: input(
          status({
            checkpoints: { structure: staleConfirmed, text: staleSkipped, titles: staleConfirmed },
          }),
        ),
        expected: {
          structure: 'stale warn:Changed since you confirmed',
          text: 'stale warn:Changed since you skipped',
          titles: 'stale warn:Changed since you confirmed',
        },
      },
      {
        name: 'Pauses: a skip row with no pauses is skipped; a stale skip row is stale',
        input: input(status({ pauseParagraphs: 0, checkpoints: { pauses: skipped } })),
        expected: { pauses: 'skipped neutral:Skipped' },
      },
      {
        name: 'Pauses: a stale skip row with no pauses',
        input: input(status({ pauseParagraphs: 0, checkpoints: { pauses: staleSkipped } })),
        expected: { pauses: 'stale warn:Changed since you skipped' },
      },
      {
        name: 'Pauses: a row outside the host matrix (confirmed) counts as no row',
        input: input(status({ pauseParagraphs: 0, checkpoints: { pauses: confirmed } })),
        expected: { pauses: 'remaining warn:No pauses yet' },
      },
      {
        name: 'Pauses edge rule: pauses exist and a skip row (stale or not): derived wins',
        input: input(status({ pauseParagraphs: 2, checkpoints: { pauses: staleSkipped } })),
        expected: { pauses: 'done ok:Done' },
      },
      {
        name: 'Speakers edge rule: review confirmed, then unassigned rises',
        input: input(
          status({
            checkpoints: { speakers: confirmed },
            nodes: { [VOL]: node({ attributionRemaining: 2 }) },
          }),
        ),
        expected: { speakers: 'remaining warn:2 paragraphs unassigned' },
      },
      {
        name: 'a rows-only project without content shows nothing started (rows are ignored)',
        input: input({ ...FRESH, checkpoints: { structure: confirmed } }),
        expected: { structure: 'notStarted neutral:Not started' },
      },
    ];

    it.each(cases)('$name', ({ input: i, expected }) => {
      for (const [id, want] of Object.entries(expected)) {
        expect(`${id} ${chip(i, id as StepId)}`).toBe(`${id} ${want}`);
      }
    });
  });

  describe('detail lines', () => {
    it('keep the texts of the six-step stepper, renamed for the new steps', () => {
      const i = input(
        status({
          charactersWithLines: 4,
          readyVoices: 1,
          items: { total: 20, withAudio: 15, unattributed: 0 },
          audio: { remaining: 5 },
          nodes: { [VOL]: node({ attributionRemaining: 2, audioRemaining: 5 }) },
        }),
        { lastBuild: { date: '19 Sep 2026', isPartial: true } },
      );
      expect(step(i, 'import').detail).toBe('Book read in · 20 items');
      expect(step(i, 'speakers').detail).toBe('2 paragraphs with unassigned speakers');
      expect(step(i, 'voices').detail).toBe(
        '1 of 4 speakers have a voice with audio · Voice rules on the Cast page',
      );
      expect(step(i, 'pauses').detail).toBe('3 pause paragraphs');
      expect(step(i, 'audio').detail).toBe('5 paragraphs missing audio · 15 of 20 items generated');
      expect(step(i, 'export').detail).toBe('Last build: 19 Sep 2026 (partial)');
    });

    it('describe the step before there is anything to count', () => {
      const i = input(FRESH);
      expect(step(i, 'import').detail).toBe('Read the book file into chapters and paragraphs.');
      expect(step(i, 'export').detail).toBe(
        'Assemble the .m4b audiobook with chapters and cover art.',
      );
      expect(step(i, 'pauses').detail).toBe(
        'Insert a pause between paragraphs and at the end of every chapter.',
      );
    });
  });

  describe('next marker', () => {
    it('is the first step not done, confirmed or skipped, in strict order', () => {
      expect(nextOf(input(FRESH))).toBe('import');
      expect(nextOf(input(status()))).toBe('structure');
      expect(nextOf(input(status({ checkpoints: { structure: skipped } })))).toBe('speakers');
      expect(nextOf(settled())).toBeNull();
    });

    it('a later derived step never jumps a conditional step', () => {
      const i = input(status({ checkpoints: { speakers: confirmed, text: confirmed } }));
      expect(nextOf(i)).toBe('structure');
    });

    it('stale is next again; running stays next', () => {
      expect(
        nextOf(
          settled({
            status: status({
              ...SETTLED,
              checkpoints: { ...SETTLED.checkpoints, titles: staleConfirmed },
            }),
          }),
        ),
      ).toBe('titles');
      expect(nextOf(settled({ assemblyRunning: true }))).toBe('export');
    });

    it('the Speakers edge rule makes Speakers next again while unassigned > 0', () => {
      const i = settled({ nodes: { [VOL]: node({ attributionRemaining: 1 }) } });
      expect(nextOf(i)).toBe('speakers');
    });

    it('the next step alone counts the later steps already done, never stale ones', () => {
      const i = input({
        ...SETTLED,
        checkpoints: { speakers: confirmed, text: skipped, titles: staleConfirmed },
      });
      // Structure is next; later: speakers, text, voices, pauses, audio done → 5 (titles stale, export not built).
      expect(step(i, 'structure').laterDone).toBe(5);
      expect(
        derivePipeline(i)
          .filter((s) => s.laterDone > 0)
          .map((s) => s.id),
      ).toEqual(['structure']);
      expect(step(input(FRESH), 'import').laterDone).toBe(0);
    });
  });

  describe('checkpoint pills', () => {
    const rowSteps: StepId[] = ['structure', 'text', 'titles'];

    it.each(rowSteps)(
      '%s: Confirm · Skip, Reopen, Reconfirm · Reopen, Reconfirm skip · Reopen',
      (id) => {
        const withRow = (row?: StepCheckpointDto) =>
          pills(input(status({ checkpoints: row ? { [id]: row } : {} })), id);
        expect(withRow()).toEqual(['confirm', 'skip']);
        expect(withRow(confirmed)).toEqual(['reopen']);
        expect(withRow(skipped)).toEqual(['reopen']);
        expect(withRow(staleConfirmed)).toEqual(['reconfirm', 'reopen']);
        expect(withRow(staleSkipped)).toEqual(['reconfirmSkip', 'reopen']);
      },
    );

    it('Pauses: Skip only while no pauses, Reopen on a skip row, none once pauses exist', () => {
      const none = status({ pauseParagraphs: 0 });
      expect(pills(input(none), 'pauses')).toEqual(['skip']);
      expect(pills(input({ ...none, checkpoints: { pauses: confirmed } }), 'pauses')).toEqual([
        'skip',
      ]);
      expect(pills(input({ ...none, checkpoints: { pauses: skipped } }), 'pauses')).toEqual([
        'reopen',
      ]);
      expect(pills(input({ ...none, checkpoints: { pauses: staleSkipped } }), 'pauses')).toEqual([
        'reconfirmSkip',
        'reopen',
      ]);
      expect(pills(input(status()), 'pauses')).toEqual([]);
      expect(pills(input(status({ checkpoints: { pauses: skipped } })), 'pauses')).toEqual([]);
    });

    it('Speakers: Confirm review, then Reopen; offered while paragraphs are still unassigned', () => {
      expect(pills(input(status()), 'speakers')).toEqual(['confirm']);
      expect(pills(input(status({ checkpoints: { speakers: confirmed } })), 'speakers')).toEqual([
        'reopen',
      ]);
      const unassigned = status({ nodes: { [VOL]: node({ attributionRemaining: 2 }) } });
      expect(pills(input(unassigned), 'speakers')).toEqual(['confirm']);
    });

    it('carry their label and the state the PUT sends', () => {
      const text = step(input(status({ checkpoints: { text: staleSkipped } })), 'text');
      expect(text.checkpoint).toEqual([
        { id: 'reconfirmSkip', label: 'Reconfirm skip', state: 'skipped' },
        { id: 'reopen', label: 'Reopen', state: 'open' },
      ]);
      const structure = step(input(status()), 'structure');
      expect(structure.checkpoint).toEqual([
        { id: 'confirm', label: 'Confirm', state: 'confirmed' },
        { id: 'skip', label: 'Skip', state: 'skipped' },
      ]);
      expect(step(input(status()), 'speakers').checkpoint[0]).toEqual({
        id: 'confirm',
        label: 'Confirm review',
        state: 'confirmed',
      });
      expect(
        step(input(status({ checkpoints: { structure: staleConfirmed } })), 'structure')
          .checkpoint[0],
      ).toEqual({
        id: 'reconfirm',
        label: 'Reconfirm',
        state: 'confirmed',
      });
    });

    it('are hidden with no content, and absent on Import, Voices, Audio and Export', () => {
      for (const s of derivePipeline(input(FRESH))) expect(s.checkpoint).toEqual([]);
      for (const id of ['import', 'voices', 'audio', 'export'] as StepId[])
        expect(pills(input(status()), id)).toEqual([]);
    });
  });

  describe('actions', () => {
    it('are the nine-step set, primary first, with discover gone', () => {
      const ids = Object.fromEntries(
        derivePipeline(input(status())).map((s) => [s.id, s.actions.map((a) => a.id)]),
      );
      expect(ids).toEqual({
        import: ['reread'],
        structure: ['checkStructure', 'manualReread'],
        speakers: ['attribute', 'openSpeakers', 'openCast'],
        text: ['reviewText', 'editWithAi'],
        voices: ['openCast'],
        titles: ['addTitles'],
        pauses: ['addPauses'],
        audio: ['generateAudio', 'openAudioMode'],
        export: ['assemble'],
      });
      expect(step(input(FRESH), 'import').actions.map((a) => a.id)).toEqual(['readBook']);
      // Import is done once there is content, so its reread stays a quiet text button.
      for (const s of derivePipeline(input(status())))
        expect(`${s.id} ${s.actions.filter((a) => a.primary).length}`).toBe(
          `${s.id} ${s.id === 'import' ? 0 : 1}`,
        );
    });

    it('label the rereads and the Book-bound actions as the spec words them', () => {
      const i = input(status());
      expect(action(i, 'import', 'reread').label).toBe('Reread… (auto splits)');
      expect(action(i, 'import', 'reread').destructive).toBe(true);
      expect(action(i, 'structure', 'checkStructure').label).toBe('Check structure');
      expect(action(i, 'structure', 'manualReread').label).toBe('Reread with split rules…');
      expect(action(i, 'structure', 'manualReread').destructive).toBe(true);
      expect(action(i, 'text', 'reviewText').label).toBe('Review text');
      expect(action(i, 'text', 'editWithAi').label).toBe('Edit with AI…');
      expect(action(i, 'titles', 'addTitles').label).toBe('Add titles…');
      expect(action(i, 'pauses', 'addPauses').label).toBe('Add pauses');
    });
  });

  describe('blocked reasons', () => {
    it('every Book-bound action, attribute, generateAudio and assemble need content', () => {
      const fresh = derivePipeline(input(FRESH));
      const blocked = fresh.flatMap((s) =>
        s.actions.filter((a) => a.blockedReason !== null).map((a) => `${a.id}:${a.blockedReason}`),
      );
      expect(blocked).toEqual([
        'checkStructure:Read the book first',
        'manualReread:Read the book first',
        'attribute:Read the book first',
        'openSpeakers:Read the book first',
        'reviewText:Read the book first',
        'editWithAi:Read the book first',
        'addTitles:Read the book first',
        'addPauses:Read the book first',
        'generateAudio:Read the book first',
        'openAudioMode:Read the book first',
        'assemble:Read the book first',
      ]);
      expect(action(input(FRESH), 'import', 'readBook').blockedReason).toBeNull();
      expect(action(input(FRESH), 'speakers', 'openCast').blockedReason).toBeNull();
    });

    it('name why nothing is left to queue, and that assembly is running', () => {
      const done = input(status());
      expect(action(done, 'speakers', 'attribute').blockedReason).toBe(
        'Every paragraph has a speaker',
      );
      expect(action(done, 'audio', 'generateAudio').blockedReason).toBe('Every item has audio');
      expect(action(done, 'export', 'assemble').blockedReason).toBeNull();
      expect(
        action(input(status(), { assemblyRunning: true }), 'export', 'assemble').blockedReason,
      ).toBe('Assembling…');
    });

    it('are null on everything else once there is content and work remains', () => {
      const working = input(
        status({
          audio: { remaining: 2 },
          nodes: { [VOL]: node({ attributionRemaining: 1, audioRemaining: 2 }) },
        }),
      );
      const blocked = derivePipeline(working).flatMap((s) =>
        s.actions.filter((a) => a.blockedReason !== null).map((a) => a.id),
      );
      expect(blocked).toEqual([]);
    });

    it('no checkpoint ever disables an action', () => {
      const stale = input(
        status({
          audio: { remaining: 2 },
          nodes: { [VOL]: node({ attributionRemaining: 1, audioRemaining: 2 }) },
          checkpoints: { structure: staleConfirmed, text: staleSkipped, titles: staleConfirmed },
        }),
      );
      for (const s of derivePipeline(stale))
        for (const a of s.actions) expect(`${a.id}:${a.blockedReason}`).toBe(`${a.id}:null`);
    });
  });
});

describe('readOnlyPipeline (ticket 32)', () => {
  const steps = () => readOnlyPipeline(derivePipeline(settled()), 'Viewing');

  it.each<[StepActionId, string | null]>([
    ['reread', 'Viewing'],
    ['manualReread', 'Viewing'],
    ['attribute', 'Viewing'],
    ['editWithAi', 'Viewing'],
    ['addTitles', 'Viewing'],
    ['addPauses', 'Viewing'],
    ['generateAudio', 'Viewing'],
    ['checkStructure', null],
    ['reviewText', null],
    ['openSpeakers', null],
    ['openCast', null],
    ['openAudioMode', null],
    ['assemble', null],
  ])('%s is blocked with %p', (id, reason) => {
    const found = steps()
      .flatMap((s) => s.actions)
      .filter((a) => a.id === id);
    expect(found.length).toBeGreaterThan(0);
    for (const a of found) {
      if (reason === null) expect(a.blockedReason).not.toBe('Viewing');
      else expect(a.blockedReason).toBe(reason);
    }
  });

  it('blocks every checkpoint pill with the reason', () => {
    const all = steps().flatMap((s) => s.checkpoint);
    expect(all.length).toBeGreaterThan(0);
    expect(all.every((p) => p.blockedReason === 'Viewing')).toBe(true);
  });
});

describe('adviceFor', () => {
  const steps = (s: ProjectStatusDto, overrides: Partial<PipelineInput> = {}) =>
    derivePipeline(input(s, { lastBuild: FULL_BUILD, ...overrides }));

  const cases: {
    name: string;
    status: ProjectStatusDto;
    action: StepActionId;
    line: string | null;
  }[] = [
    {
      name: 'generateAudio names every unsettled one of Text, Titles and Pauses',
      status: status({ pauseParagraphs: 0, checkpoints: { structure: confirmed } }),
      action: 'generateAudio',
      line: "Text, Titles or Pauses aren't settled yet: changes there will need new audio.",
    },
    {
      name: 'generateAudio names one',
      status: status({ checkpoints: { text: confirmed, titles: staleConfirmed, pauses: skipped } }),
      action: 'generateAudio',
      line: "Titles aren't settled yet: changes there will need new audio.",
    },
    {
      name: 'generateAudio names Text alone in the singular',
      status: status({ checkpoints: { titles: confirmed } }),
      action: 'generateAudio',
      line: "Text isn't settled yet: changes there will need new audio.",
    },
    {
      name: 'generateAudio names two of three',
      status: status({ pauseParagraphs: 0, checkpoints: { titles: skipped } }),
      action: 'generateAudio',
      line: "Text or Pauses aren't settled yet: changes there will need new audio.",
    },
    {
      name: 'generateAudio says nothing once Text, Titles and Pauses are settled',
      status: SETTLED,
      action: 'generateAudio',
      line: null,
    },
    {
      name: 'attribute before Structure is settled',
      status: status(),
      action: 'attribute',
      line: 'Usually done after Structure: a reread discards attribution.',
    },
    {
      name: 'attribute after Structure is skipped',
      status: status({ checkpoints: { structure: skipped } }),
      action: 'attribute',
      line: null,
    },
    {
      name: 'addTitles before Structure is settled',
      status: status({ checkpoints: { structure: staleConfirmed } }),
      action: 'addTitles',
      line: 'Usually done after Structure: a reread drops inserted titles.',
    },
    {
      name: 'addTitles when titles were already confirmed',
      status: status({ checkpoints: { structure: confirmed, titles: confirmed } }),
      action: 'addTitles',
      line: 'You confirmed titles already: adding again inserts a second copy.',
    },
    {
      name: 'addTitles with both conditions holding says both',
      status: status({ checkpoints: { titles: confirmed } }),
      action: 'addTitles',
      line:
        'Usually done after Structure: a reread drops inserted titles. ' +
        'You confirmed titles already: adding again inserts a second copy.',
    },
    {
      name: 'addPauses before Structure is settled',
      status: status({ pauseParagraphs: 0 }),
      action: 'addPauses',
      line: 'Usually done after Structure: a reread drops inserted pauses.',
    },
    {
      name: 'addPauses once Structure is confirmed',
      status: status({ pauseParagraphs: 0, checkpoints: { structure: confirmed } }),
      action: 'addPauses',
      line: null,
    },
    {
      name: 'other actions carry no advice',
      status: status(),
      action: 'openSpeakers',
      line: null,
    },
  ];

  it.each(cases)('$name', ({ status: s, action: a, line }) => {
    expect(adviceFor(a, steps(s))).toBe(line);
  });

  it('a conditional step without content is unsettled, like one with no row', () => {
    expect(adviceFor('attribute', steps(FRESH))).toBe(
      'Usually done after Structure: a reread discards attribution.',
    );
  });

  it('each derived step carries its own advice line, from its actions', () => {
    const rows = steps(status({ pauseParagraphs: 0 }));
    const advice = (id: string) => rows.find((s) => s.id === id)?.advice;
    expect(advice('titles')).toBe('Usually done after Structure: a reread drops inserted titles.');
    expect(advice('pauses')).toBe('Usually done after Structure: a reread drops inserted pauses.');
    expect(advice('import')).toBeNull();
  });

  it('a Pauses step that is derived-done counts as settled', () => {
    const s = status({ checkpoints: { text: confirmed, titles: confirmed } });
    expect(adviceFor('generateAudio', steps(s))).toBeNull();
  });
});
