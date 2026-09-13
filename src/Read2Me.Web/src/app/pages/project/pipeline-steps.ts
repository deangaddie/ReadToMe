import { ProjectStatusDto } from '@app/api';
import { NodeStatusSummary } from '@app/live/live-messages';
import { PipelineAction, PipelineStepView } from '@app/ui/pipeline/pipeline';
import { StatusKind } from '@app/ui/status-chip/status-chip';

/**
 * The six-step production pipeline on the project overview (design §6.2), derived from the
 * `/status` bootstrap plus what the hub has said since. Pure, so the stepper's rules live in one
 * table-tested function and the component only renders.
 */

export type StepId = 'import' | 'cast' | 'attribute' | 'voices' | 'audio' | 'export';

export type StepState = 'notStarted' | 'remaining' | 'running' | 'done';

export type StepActionId =
  | 'readBook'
  | 'reread'
  | 'discover'
  | 'openCast'
  | 'attribute'
  | 'openSpeakers'
  | 'generateAudio'
  | 'openAudioMode'
  | 'assemble';

export interface StepAction extends PipelineAction {
  id: StepActionId;
}

export interface PipelineStep extends PipelineStepView {
  id: StepId;
  state: StepState;
  actions: StepAction[];
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
}

const CHIP: Record<
  Exclude<StepState, 'remaining' | 'running'>,
  { status: StatusKind; label: string }
> = {
  notStarted: { status: 'neutral', label: 'Not started' },
  done: { status: 'ok', label: 'Done' },
};

function chipFor(state: StepState, label?: string): { status: StatusKind; label: string } {
  switch (state) {
    case 'remaining':
      return { status: 'warn', label: label ?? 'Remaining' };
    case 'running':
      return { status: 'busy', label: label ?? 'Running' };
    default:
      return CHIP[state];
  }
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}

/** Book-wide attribution from the volume roll-ups, which partition the book. */
function attributionTotals(input: PipelineInput): {
  remaining: number;
  queued: number;
  processing: boolean;
} {
  const { volumeIds } = input.status;
  let remaining = 0;
  let queued = 0;
  let processing = false;
  for (const id of volumeIds) {
    const n = input.nodes[id];
    if (!n) continue;
    remaining += n.attributionRemaining;
    queued += n.attributionQueued;
    processing ||= n.attributionProcessing;
  }
  return { remaining, queued, processing };
}

export function derivePipeline(input: PipelineInput): PipelineStep[] {
  const s = input.status;
  const content = s.hasContent;

  const importState: StepState = content ? 'done' : 'notStarted';

  const castState: StepState = content && s.characters > 0 ? 'done' : 'notStarted';

  const attribution = attributionTotals(input);
  const attributionRunning = attribution.queued > 0 || attribution.processing;
  const attributeState: StepState = attributionRunning
    ? 'running'
    : !content
      ? 'notStarted'
      : attribution.remaining > 0
        ? 'remaining'
        : 'done';
  const attributeLabel = attributionRunning
    ? attribution.processing
      ? `${attribution.queued} queued · processing`
      : `${attribution.queued} queued`
    : `${attribution.remaining} remaining`;

  const voicesMissing = Math.max(0, s.charactersWithLines - s.readyVoices);
  const voicesState: StepState = input.voiceBatchRunning
    ? 'running'
    : s.charactersWithLines === 0
      ? 'notStarted'
      : voicesMissing > 0
        ? 'remaining'
        : 'done';

  const audioRemaining = input.folderAudioRemaining;
  const audioState: StepState =
    input.audioInFlight > 0
      ? 'running'
      : !content || s.items.total === 0
        ? 'notStarted'
        : audioRemaining > 0
          ? 'remaining'
          : 'done';
  const audioLabel =
    audioState === 'running' ? `${input.audioInFlight} in flight` : `${audioRemaining} remaining`;

  const exportState: StepState = input.assemblyRunning ? 'running' : 'notStarted';

  const steps: Omit<PipelineStep, 'next'>[] = [
    {
      id: 'import',
      title: 'Import',
      icon: 'upload_file',
      state: importState,
      chip: chipFor(importState),
      detail: content
        ? `Book read in · ${plural(s.items.total, 'item')}`
        : 'Read the book file into chapters and paragraphs.',
      actions: content
        ? [
            {
              id: 'reread',
              label: 'Reread…',
              icon: 'restart_alt',
              primary: false,
              destructive: true,
              disabled: false,
            },
          ]
        : [
            {
              id: 'readBook',
              label: 'Read book',
              icon: 'menu_book',
              primary: true,
              disabled: false,
            },
          ],
    },
    {
      id: 'cast',
      title: 'Cast',
      icon: 'groups',
      state: castState,
      chip: chipFor(castState),
      detail: s.characters > 0 ? plural(s.characters, 'character') : 'Find who speaks in the book.',
      actions: [
        {
          id: 'discover',
          label: 'Discover characters',
          icon: 'person_search',
          primary: true,
          disabled: !content,
        },
        { id: 'openCast', label: 'Open cast', icon: 'groups', primary: false, disabled: false },
      ],
    },
    {
      id: 'attribute',
      title: 'Attribute',
      icon: 'record_voice_over',
      state: attributeState,
      chip: chipFor(attributeState, attributeLabel),
      detail: content
        ? `${plural(attribution.remaining, 'paragraph')} with unassigned speakers`
        : 'Assign a speaker to every line.',
      actions: [
        {
          id: 'attribute',
          label: 'Attribute unprocessed',
          icon: 'auto_awesome',
          primary: true,
          disabled: !content || attribution.remaining === 0,
        },
        {
          id: 'openSpeakers',
          label: 'Open in Speakers mode',
          icon: 'menu_book',
          primary: false,
          disabled: !content,
        },
      ],
    },
    {
      id: 'voices',
      title: 'Voices',
      icon: 'graphic_eq',
      state: voicesState,
      chip: chipFor(
        voicesState,
        voicesState === 'remaining' ? `${voicesMissing} remaining` : undefined,
      ),
      detail:
        s.charactersWithLines > 0
          ? `${s.readyVoices} of ${s.charactersWithLines} speakers have a voice with audio`
          : 'Speakers need a voice before audio can be generated.',
      actions: [
        { id: 'openCast', label: 'Open cast', icon: 'groups', primary: true, disabled: false },
      ],
    },
    {
      id: 'audio',
      title: 'Audio',
      icon: 'volume_up',
      state: audioState,
      chip: chipFor(audioState, audioLabel),
      detail:
        content && s.items.total > 0
          ? `${plural(audioRemaining, 'paragraph')} missing audio · ${s.items.withAudio} of ${plural(s.items.total, 'item')} generated`
          : 'Synthesise every line.',
      actions: [
        {
          id: 'generateAudio',
          label: 'Generate needed audio',
          icon: 'play_circle',
          primary: true,
          disabled: !content || audioRemaining === 0,
        },
        {
          id: 'openAudioMode',
          label: 'Open in Audio mode',
          icon: 'menu_book',
          primary: false,
          disabled: !content,
        },
      ],
    },
    {
      id: 'export',
      title: 'Export',
      icon: 'download',
      state: exportState,
      chip: chipFor(exportState),
      detail: 'Assemble the .m4b audiobook with chapters and cover art.',
      actions: [
        {
          id: 'assemble',
          label: 'Assemble',
          icon: 'library_music',
          primary: true,
          disabled: !content,
        },
      ],
    },
  ];

  const nextIndex = steps.findIndex((step) => step.state !== 'done');
  return steps.map((step, i) => ({ ...step, next: i === nextIndex }));
}
