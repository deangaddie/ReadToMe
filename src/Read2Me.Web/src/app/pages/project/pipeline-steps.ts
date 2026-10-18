import type {
  CheckpointRequestState,
  PipelineStepId,
  ProjectStatusDto,
  StepCheckpointDto,
} from '@app/api';
import type { NodeStatusSummary } from '@app/live/live-messages';
import type { StatusKind } from '@app/ui/partials';
import type { PipelineAction, PipelineStepView } from '@app/ui/pipeline';

/**
 * The nine Pipeline steps on the project overview (guidance-primitives spec §2), derived from the
 * `/status` bootstrap plus what the hub has said since. Pure, so the stepper's rules live in one
 * table-tested function; `ProjectStore` owns the input and every project page reads the result.
 */

export type StepId = PipelineStepId;

export type StepState =
  | 'notStarted'
  | 'remaining'
  | 'running'
  | 'unconfirmed'
  | 'done'
  | 'confirmed'
  | 'skipped'
  | 'stale';

export type StepActionId =
  | 'readBook'
  | 'reread'
  | 'checkStructure'
  | 'manualReread'
  | 'attribute'
  | 'openSpeakers'
  | 'openCast'
  | 'reviewText'
  | 'editWithAi'
  | 'addTitles'
  | 'addPauses'
  | 'generateAudio'
  | 'openAudioMode'
  | 'assemble';

export interface StepAction extends PipelineAction {
  id: StepActionId;
}

export type CheckpointPillId = 'confirm' | 'skip' | 'reopen' | 'reconfirm' | 'reconfirmSkip';

/** One checkpoint pill beside the chip: its click PUTs `state`. */
export interface CheckpointPill {
  id: CheckpointPillId;
  label: string;
  state: CheckpointRequestState;
}

export interface PipelineStep extends PipelineStepView {
  id: StepId;
  state: StepState;
  actions: StepAction[];
  /** Hidden (empty) while there is no content or the step has no checkpoint. */
  checkpoint: CheckpointPill[];
  /** On the next step only: later steps already done, confirmed or skipped (never stale). */
  laterDone: number;
}

export interface PipelineInput {
  status: ProjectStatusDto;
  /** Node roll-ups as currently known: the bootstrap map with hub deltas applied. */
  nodes: Record<string, NodeStatusSummary | null>;
  /** Paragraphs missing audio, from the hub when it has spoken, else the bootstrap. */
  folderAudioRemaining: number;
  /** This project's audio items the queue reports queued or processing. */
  audioInFlight: number;
  voiceBatchRunning: boolean;
  assemblyRunning: boolean;
  /** The newest assembled audiobook, its date already formatted for display; null when there is none. */
  lastBuild: { date: string; isPartial: boolean } | null;
}

const STATUS_OF: Record<StepState, StatusKind> = {
  notStarted: 'neutral',
  remaining: 'warn',
  running: 'busy',
  unconfirmed: 'neutral',
  done: 'ok',
  confirmed: 'ok',
  skipped: 'neutral',
  stale: 'warn',
};

/** Done by derivation or by the user: the step holds no attention. */
const SETTLED: ReadonlySet<StepState> = new Set(['done', 'confirmed', 'skipped']);

export const READ_THE_BOOK_FIRST = 'Read the book first';

/** A step's state and the chip text that goes with it, before the colour is looked up. */
type Outcome = { state: StepState; label: string };

/** A step's ordered done conditions: the first unmet one is the outcome; all held gives `done`. */
type Condition = { holds: boolean; unmet: Outcome };

function resolve(conditions: Condition[], done: Outcome): Outcome {
  return conditions.find((c) => !c.holds)?.unmet ?? done;
}

const SKIPPED: Outcome = { state: 'skipped', label: 'Skipped' };

/** What a row says once the derived conditions before it hold. */
function rowOutcome(row: StepCheckpointDto): Outcome {
  if (row.stale) return { state: 'stale', label: `Changed since you ${row.state}` };
  return row.state === 'confirmed' ? { state: 'confirmed', label: 'Confirmed' } : SKIPPED;
}

const REOPEN: CheckpointPill = { id: 'reopen', label: 'Reopen', state: 'open' };

/** The pill set for a Structure / Text / Titles row, and Pauses' skip-only subset of it. */
function rowPills(row: StepCheckpointDto | undefined, confirmable: boolean): CheckpointPill[] {
  const confirm: CheckpointPill = { id: 'confirm', label: 'Confirm', state: 'confirmed' };
  const skip: CheckpointPill = { id: 'skip', label: 'Skip', state: 'skipped' };
  if (!row) return confirmable ? [confirm, skip] : [skip];
  if (!row.stale) return [REOPEN];
  return row.state === 'confirmed'
    ? [{ id: 'reconfirm', label: 'Reconfirm', state: 'confirmed' }, REOPEN]
    : [{ id: 'reconfirmSkip', label: 'Reconfirm skip', state: 'skipped' }, REOPEN];
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Book-wide attribution from the volume roll-ups, which partition the book. */
function attributionTotals(input: PipelineInput): {
  unassigned: number;
  queued: number;
  processing: boolean;
} {
  const { volumeIds } = input.status;
  let unassigned = 0;
  let queued = 0;
  let processing = false;
  for (const id of volumeIds) {
    const n = input.nodes[id];
    if (!n) continue;
    unassigned += n.attributionRemaining;
    queued += n.attributionQueued;
    processing ||= n.attributionProcessing;
  }
  return { unassigned, queued, processing };
}

/** A step before the next marker, the hint and the chip colour are added. */
type Draft = Omit<PipelineStep, 'next' | 'laterDone' | 'chip' | 'state'> & { outcome: Outcome };

export function derivePipeline(input: PipelineInput): PipelineStep[] {
  const s = input.status;
  const content = s.hasContent;
  const rows = content ? s.checkpoints : {};
  const needsContent = (): string | null => (content ? null : READ_THE_BOOK_FIRST);
  const notStarted: Outcome = { state: 'notStarted', label: 'Not started' };
  const contentCondition: Condition = { holds: content, unmet: notStarted };
  const done: Outcome = { state: 'done', label: 'Done' };

  const attribution = attributionTotals(input);
  const attributionRunning = attribution.queued > 0 || attribution.processing;
  const voicesMissing = Math.max(0, s.charactersWithLines - s.readyVoices);
  const audioMissing = input.folderAudioRemaining;
  const hasPauses = s.pauseParagraphs > 0;
  const fullBuild = input.lastBuild !== null && !input.lastBuild.isPartial;

  const conditional = (
    id: 'structure' | 'text' | 'titles',
  ): Pick<Draft, 'outcome' | 'checkpoint'> => {
    const row = rows[id];
    return {
      outcome: resolve(
        [
          contentCondition,
          { holds: !!row, unmet: { state: 'unconfirmed', label: 'Not confirmed' } },
        ],
        row ? rowOutcome(row) : done,
      ),
      checkpoint: content ? rowPills(row, true) : [],
    };
  };

  const speakersRow = rows.speakers;
  const speakersOutcome = resolve(
    [
      contentCondition,
      {
        holds: !attributionRunning,
        unmet: {
          state: 'running',
          label: attribution.processing
            ? `${attribution.queued} queued · processing`
            : `${attribution.queued} queued`,
        },
      },
      {
        holds: attribution.unassigned === 0,
        unmet: {
          state: 'remaining',
          label: `${plural(attribution.unassigned, 'paragraph')} unassigned`,
        },
      },
      { holds: !!speakersRow, unmet: { state: 'unconfirmed', label: 'Review not confirmed' } },
    ],
    { state: 'confirmed', label: 'Review confirmed' },
  );

  // Pauses: once pauses exist the step is done whatever its row says (spec §2 edge rule).
  // The host allows Pauses only a skip row; anything else is treated as no row rather than mislabelled.
  const pausesRow = rows.pauses?.state === 'skipped' ? rows.pauses : undefined;
  const pausesOutcome = resolve(
    [
      contentCondition,
      {
        holds: hasPauses || !!pausesRow,
        unmet: { state: 'remaining', label: 'No pauses yet' },
      },
    ],
    hasPauses || !pausesRow ? done : rowOutcome(pausesRow),
  );

  const steps: Draft[] = [
    {
      id: 'import',
      title: 'Import',
      icon: 'upload_file',
      outcome: resolve([contentCondition], done),
      detail: content
        ? `Book read in · ${plural(s.items.total, 'item')}`
        : 'Read the book file into chapters and paragraphs.',
      checkpoint: [],
      actions: content
        ? [
            {
              id: 'reread',
              label: 'Reread… (auto splits)',
              icon: 'restart_alt',
              primary: false,
              destructive: true,
              blockedReason: null,
            },
          ]
        : [
            {
              id: 'readBook',
              label: 'Read book',
              icon: 'menu_book',
              primary: true,
              blockedReason: null,
            },
          ],
    },
    {
      id: 'structure',
      title: 'Structure',
      icon: 'call_split',
      ...conditional('structure'),
      detail: content
        ? 'Check that volumes, parts and chapters split where the book does.'
        : 'Check the chapters once the book is read in.',
      actions: [
        {
          id: 'checkStructure',
          label: 'Check structure',
          icon: 'menu_book',
          primary: true,
          blockedReason: needsContent(),
        },
        {
          id: 'manualReread',
          label: 'Reread with split rules…',
          icon: 'tune',
          primary: false,
          destructive: true,
          blockedReason: needsContent(),
        },
      ],
    },
    {
      id: 'speakers',
      title: 'Speakers',
      icon: 'record_voice_over',
      outcome: speakersOutcome,
      checkpoint: !content
        ? []
        : speakersRow
          ? [REOPEN]
          : [{ id: 'confirm', label: 'Confirm review', state: 'confirmed' }],
      detail: content
        ? `${plural(attribution.unassigned, 'paragraph')} with unassigned speakers`
        : 'Assign a speaker to every line.',
      actions: [
        {
          id: 'attribute',
          label: 'Attribute unprocessed',
          icon: 'auto_awesome',
          primary: true,
          blockedReason:
            needsContent() ??
            (attribution.unassigned === 0 ? 'Every paragraph has a speaker' : null),
        },
        {
          id: 'openSpeakers',
          label: 'Open in Speakers mode',
          icon: 'menu_book',
          primary: false,
          blockedReason: needsContent(),
        },
        { id: 'openCast', label: 'Open cast', icon: 'groups', primary: false, blockedReason: null },
      ],
    },
    {
      id: 'text',
      title: 'Text',
      icon: 'edit',
      ...conditional('text'),
      detail: content
        ? 'Read for stray page numbers, footnotes and broken words.'
        : 'Polish the text once the book is read in.',
      actions: [
        {
          id: 'reviewText',
          label: 'Review text',
          icon: 'menu_book',
          primary: true,
          blockedReason: needsContent(),
        },
        {
          id: 'editWithAi',
          label: 'Edit with AI…',
          icon: 'auto_fix_high',
          primary: false,
          blockedReason: needsContent(),
        },
      ],
    },
    {
      id: 'voices',
      title: 'Voices',
      icon: 'graphic_eq',
      outcome: resolve(
        [
          { holds: s.charactersWithLines > 0, unmet: notStarted },
          { holds: !input.voiceBatchRunning, unmet: { state: 'running', label: 'Running' } },
          {
            holds: voicesMissing === 0,
            unmet: {
              state: 'remaining',
              label: `${s.readyVoices} of ${s.charactersWithLines} speakers voiced`,
            },
          },
        ],
        done,
      ),
      checkpoint: [],
      detail:
        s.charactersWithLines > 0
          ? `${s.readyVoices} of ${s.charactersWithLines} speakers have a voice with audio · Voice rules on the Cast page`
          : 'Speakers need a voice before audio can be generated.',
      actions: [
        { id: 'openCast', label: 'Open cast', icon: 'groups', primary: true, blockedReason: null },
      ],
    },
    {
      id: 'titles',
      title: 'Titles',
      icon: 'title',
      ...conditional('titles'),
      detail: content
        ? 'Spoken titles announce the book, its volumes, parts and chapters.'
        : 'Add spoken titles once the book is read in.',
      actions: [
        {
          id: 'addTitles',
          label: 'Add titles…',
          icon: 'title',
          primary: true,
          blockedReason: needsContent(),
        },
      ],
    },
    {
      id: 'pauses',
      title: 'Pauses',
      icon: 'pause',
      outcome: pausesOutcome,
      checkpoint: !content || hasPauses ? [] : rowPills(pausesRow, false),
      detail:
        content && hasPauses
          ? `${plural(s.pauseParagraphs, 'pause paragraph')}`
          : 'Insert a pause after every chapter title.',
      actions: [
        {
          id: 'addPauses',
          label: 'Add pauses',
          icon: 'pause',
          primary: true,
          blockedReason: needsContent(),
        },
      ],
    },
    {
      id: 'audio',
      title: 'Audio',
      icon: 'volume_up',
      outcome: resolve(
        [
          { holds: content && s.items.total > 0, unmet: notStarted },
          {
            holds: input.audioInFlight === 0,
            unmet: { state: 'running', label: `${input.audioInFlight} in flight` },
          },
          {
            holds: audioMissing === 0,
            unmet: {
              state: 'remaining',
              label: `${plural(audioMissing, 'paragraph')} missing audio`,
            },
          },
        ],
        done,
      ),
      checkpoint: [],
      detail:
        content && s.items.total > 0
          ? `${plural(audioMissing, 'paragraph')} missing audio · ${s.items.withAudio} of ${plural(s.items.total, 'item')} generated`
          : 'Synthesise every line.',
      actions: [
        {
          id: 'generateAudio',
          label: 'Generate needed audio',
          icon: 'play_circle',
          primary: true,
          blockedReason: needsContent() ?? (audioMissing === 0 ? 'Every item has audio' : null),
        },
        {
          id: 'openAudioMode',
          label: 'Open in Audio mode',
          icon: 'menu_book',
          primary: false,
          blockedReason: needsContent(),
        },
      ],
    },
    {
      id: 'export',
      title: 'Export',
      icon: 'download',
      outcome: resolve(
        [
          { holds: !input.assemblyRunning, unmet: { state: 'running', label: 'Assembling…' } },
          {
            holds: fullBuild,
            unmet: {
              state: 'notStarted',
              label: input.lastBuild ? 'Partial build only' : 'Not built',
            },
          },
        ],
        done,
      ),
      checkpoint: [],
      detail: input.lastBuild
        ? `Last build: ${input.lastBuild.date}${input.lastBuild.isPartial ? ' (partial)' : ''}`
        : 'Assemble the .m4b audiobook with chapters and cover art.',
      actions: [
        {
          id: 'assemble',
          label: 'Assemble',
          icon: 'library_music',
          primary: true,
          blockedReason: needsContent() ?? (input.assemblyRunning ? 'Assembling…' : null),
        },
      ],
    },
  ];

  const nextIndex = steps.findIndex((step) => !SETTLED.has(step.outcome.state));
  return steps.map(({ outcome, ...step }, i) => ({
    ...step,
    state: outcome.state,
    chip: { status: STATUS_OF[outcome.state], label: outcome.label },
    next: i === nextIndex,
    laterDone:
      i === nextIndex ? steps.slice(i + 1).filter((s) => SETTLED.has(s.outcome.state)).length : 0,
  }));
}

/** A step with no row, or a stale one, or a derived count still unmet: not done in any sense. */
function unsettled(steps: readonly PipelineStep[], id: StepId): boolean {
  const step = steps.find((s) => s.id === id);
  return step !== undefined && !SETTLED.has(step.state);
}

/**
 * The one sentence an out-of-order action shows before it is clicked (guidance-primitives spec
 * §2), or null. Shared by the Overview rows, the next-step pill, the Book overflow and the Add
 * titles dialog, so each surface says the same thing.
 */
export function adviceFor(action: StepActionId, steps: readonly PipelineStep[]): string | null {
  const structureUnsettled = unsettled(steps, 'structure');
  switch (action) {
    case 'generateAudio': {
      const names = (['text', 'titles', 'pauses'] as const)
        .filter((id) => unsettled(steps, id))
        .map((id) => steps.find((s) => s.id === id)!.title);
      if (names.length === 0) return null;
      const list =
        names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} or ${names.at(-1)}`;
      // "Text" is the one singular name; "Titles" and "Pauses" take the plural verb on their own.
      const verb = names.length === 1 && names[0] === 'Text' ? "isn't" : "aren't";
      return `${list} ${verb} settled yet: changes there will need new audio.`;
    }
    case 'attribute':
      return structureUnsettled
        ? 'Usually done after Structure: a reread discards attribution.'
        : null;
    case 'addTitles': {
      const lines = [
        structureUnsettled ? 'Usually done after Structure: a reread drops inserted titles.' : null,
        steps.find((s) => s.id === 'titles')?.state === 'confirmed'
          ? 'You confirmed titles already: adding again inserts a second copy.'
          : null,
      ].filter((l): l is string => l !== null);
      return lines.length ? lines.join(' ') : null;
    }
    case 'addPauses':
      return structureUnsettled
        ? 'Usually done after Structure: a reread drops inserted pauses.'
        : null;
    default:
      return null;
  }
}
