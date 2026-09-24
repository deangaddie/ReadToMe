import { ProjectStatusDto } from '@app/api';
import { NodeStatusSummary } from '@app/live/live-messages';
import { PipelineInput, StepId, derivePipeline } from './pipeline-steps';

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

function chips(i: PipelineInput): Record<StepId, string> {
  return Object.fromEntries(
    derivePipeline(i).map((s) => [s.id, `${s.chip.status}:${s.chip.label}`]),
  ) as Record<StepId, string>;
}

describe('derivePipeline', () => {
  const cases: { name: string; input: PipelineInput; expected: Partial<Record<StepId, string>> }[] =
    [
      {
        name: 'fresh project: nothing started',
        input: input(FRESH),
        expected: {
          import: 'neutral:Not started',
          cast: 'neutral:Not started',
          attribute: 'neutral:Not started',
          voices: 'neutral:Not started',
          audio: 'neutral:Not started',
          export: 'neutral:Not started',
        },
      },
      {
        name: 'imported, nothing else done',
        input: input(
          status({
            characters: 0,
            charactersWithLines: 1,
            readyVoices: 0,
            items: { total: 10, withAudio: 0, unattributed: 4 },
            audio: { remaining: 6 },
            nodes: { [VOL]: node({ attributionRemaining: 3, audioRemaining: 6 }) },
          }),
        ),
        expected: {
          import: 'ok:Done',
          cast: 'neutral:Not started',
          attribute: 'warn:3 remaining',
          voices: 'warn:1 remaining',
          audio: 'warn:6 remaining',
          export: 'neutral:Not started',
        },
      },
      {
        name: 'everything done',
        input: input(status()),
        expected: {
          import: 'ok:Done',
          cast: 'ok:Done',
          attribute: 'ok:Done',
          voices: 'ok:Done',
          audio: 'ok:Done',
          export: 'neutral:Not started',
        },
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
        expected: { attribute: 'busy:4 queued · processing' },
      },
      {
        name: 'live node deltas win over the bootstrap attribution count',
        input: input(status({ attribution: { remaining: 9, processing: false, queued: 0 } }), {
          nodes: { [VOL]: node({ attributionRemaining: 2 }) },
        }),
        expected: { attribute: 'warn:2 remaining' },
      },
      {
        name: 'live folder audio remaining wins over the bootstrap',
        input: input(status({ audio: { remaining: 5 } }), { folderAudioRemaining: 0 }),
        expected: { audio: 'ok:Done' },
      },
      {
        name: 'audio items in flight, voice batch and assembly running',
        input: input(status({ audio: { remaining: 3 } }), {
          audioInFlight: 7,
          voiceBatchRunning: true,
          assemblyRunning: true,
        }),
        expected: { audio: 'busy:7 in flight', voices: 'busy:Running', export: 'busy:Running' },
      },
      {
        name: 'imported book with no audio items is not "done"',
        input: input(status({ items: { total: 0, withAudio: 0, unattributed: 0 } })),
        expected: { audio: 'neutral:Not started' },
      },
    ];

  it.each(cases)('$name', ({ input: i, expected }) => {
    const actual = chips(i);
    for (const [id, chip] of Object.entries(expected)) {
      expect(`${id} ${actual[id as StepId]}`).toBe(`${id} ${chip}`);
    }
  });

  it('marks the first unfinished step as next', () => {
    expect(derivePipeline(input(FRESH)).find((s) => s.next)?.id).toBe('import');
    expect(derivePipeline(input(status({ characters: 0 }))).find((s) => s.next)?.id).toBe('cast');
    expect(
      derivePipeline(input(status()))
        .filter((s) => s.next)
        .map((s) => s.id),
    ).toEqual(['export']);
  });

  it('shows the last build on the export step; only a full build finishes it', () => {
    const exportStep = (i: PipelineInput) => derivePipeline(i).find((s) => s.id === 'export')!;

    const none = exportStep(input(status()));
    expect(none.state).toBe('notStarted');
    expect(none.detail).toBe('Assemble the .m4b audiobook with chapters and cover art.');

    const full = exportStep(
      input(status(), { lastBuild: { date: '19 Sep 2026', isPartial: false } }),
    );
    expect(full.state).toBe('done');
    expect(full.detail).toBe('Last build: 19 Sep 2026');

    const partial = exportStep(
      input(status(), { lastBuild: { date: '19 Sep 2026', isPartial: true } }),
    );
    expect(partial.state).toBe('notStarted');
    expect(partial.detail).toBe('Last build: 19 Sep 2026 (partial)');

    const running = exportStep(
      input(status(), {
        assemblyRunning: true,
        lastBuild: { date: '19 Sep 2026', isPartial: false },
      }),
    );
    expect(running.state).toBe('running');
  });

  it('offers Read book before import and a destructive Reread after', () => {
    const before = derivePipeline(input(FRESH))[0]!.actions.map((a) => a.id);
    expect(before).toEqual(['readBook']);
    const after = derivePipeline(input(status()))[0]!.actions;
    expect(after.map((a) => a.id)).toEqual(['reread']);
    expect(after[0]!.destructive).toBe(true);
  });

  it('disables book-bound actions until there is content and when nothing remains', () => {
    const fresh = derivePipeline(input(FRESH));
    const attribute = fresh.find((s) => s.id === 'attribute')!;
    expect(attribute.actions.every((a) => a.disabled)).toBe(true);

    const done = derivePipeline(input(status()));
    expect(
      done.find((s) => s.id === 'attribute')!.actions.find((a) => a.id === 'attribute')!.disabled,
    ).toBe(true);
    expect(
      done.find((s) => s.id === 'audio')!.actions.find((a) => a.id === 'generateAudio')!.disabled,
    ).toBe(true);
    expect(
      done.find((s) => s.id === 'export')!.actions.find((a) => a.id === 'assemble')!.disabled,
    ).toBe(false);
  });
});
