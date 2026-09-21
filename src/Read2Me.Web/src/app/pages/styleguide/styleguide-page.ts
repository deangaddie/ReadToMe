import { JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { ParagraphDto, ParagraphItemDto, ProjectSummary } from '@app/api';
import { AudioGenEvent, LlmStreamEvent } from '@app/live/hub-events';
import { LIVE_FAMILIES, LiveFamily, ThroughputSnapshot } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { SpeakerChip, SpeakerChipState } from '@app/ui/speaker-chip/speaker-chip';
import { SpeakerMenu, SpeakerRosterEntry } from '@app/ui/speaker-menu/speaker-menu';
import { PromptService } from '@app/ui/text-prompt-dialog/text-prompt-dialog';
import { ConfigEditorFrame } from '@app/ui/config-editor-frame/config-editor-frame';
import { ConfigList, ConfigListItem } from '@app/ui/config-list/config-list';
import { CountBadge } from '@app/ui/count-badge/count-badge';
import { AiServiceStatus, DockerControls } from '@app/ui/docker-controls/docker-controls';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { FileDrop, RejectedFile } from '@app/ui/file-drop/file-drop';
import { InlineEdit } from '@app/ui/inline-edit/inline-edit';
import { JobCard } from '@app/ui/job-card/job-card';
import { JobView } from '@app/ui/job-pill/job';
import { JobPill } from '@app/ui/job-pill/job-pill';
import { KeyValue, KeyValueRow } from '@app/ui/key-value/key-value';
import { PageHeader } from '@app/ui/page-header/page-header';
import { ProjectCard } from '@app/ui/project-card/project-card';
import { Pipeline, PipelineActionEvent } from '@app/ui/pipeline/pipeline';
import { derivePipeline } from '@app/pages/project/pipeline-steps';
import { BookEditor } from '@app/pages/book/book-editor';
import { AudioGenerator } from '@app/pages/book/audio-generator';
import { AudioSelectionStore } from '@app/pages/book/audio-selection-store';
import { SelectionStore } from '@app/pages/book/selection-store';
import { SpeakerAssigner } from '@app/pages/book/speaker-assigner';
import { ParagraphRow } from '@app/pages/book/paragraph-row';
import { ReaderMode, RowContext } from '@app/pages/book/reader-rows';
import {
  PreflightPlan,
  PreflightServiceProgress,
  PreflightSheet,
} from '@app/ui/preflight-sheet/preflight-sheet';
import { SettingsForm, SettingsSchema, SettingsValues } from '@app/ui/settings-form/settings-form';
import { Sparkline } from '@app/ui/sparkline/sparkline';
import { Throughput } from '@app/ui/throughput/throughput';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';
import { StreamAudio } from '@app/ui/stream-audio/stream-audio';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { ToastService } from '@app/ui/toast/toast.service';
import { Scheme, Story } from './story';
import { TokensTable } from './tokens-table';

type SchemeChoice = 'both' | Scheme;

interface TocEntry {
  anchor: string;
  label: string;
}

/**
 * Living style guide (design §10): every `r2m-*` component in every state, rendered per scheme,
 * plus the token table. The acceptance surface for ticket 03 and the regression surface after.
 */
@Component({
  selector: 'app-styleguide-page',
  imports: [
    JsonPipe,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    PageHeader,
    ProjectCard,
    Pipeline,
    ParagraphRow,
    EmptyState,
    StatusChip,
    CountBadge,
    KeyValue,
    InlineEdit,
    FileDrop,
    Sparkline,
    Throughput,
    JobPill,
    JobCard,
    ConfigList,
    ConfigEditorFrame,
    SettingsForm,
    DockerControls,
    PreflightSheet,
    StreamLlm,
    StreamAudio,
    SpeakerChip,
    SpeakerMenu,
    AudioPlayer,
    Story,
    TokensTable,
  ],
  templateUrl: './styleguide-page.html',
  styleUrl: './styleguide-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    // The paragraph story renders row menus and speaker chips; here they must never write anything.
    { provide: BookEditor, useValue: { locked: signal(false), run: async () => false } },
    SelectionStore,
    AudioSelectionStore,
    {
      provide: AudioGenerator,
      useValue: {
        retry: async () => false,
        dismissReview: async () => false,
        working: signal(false),
      },
    },
    {
      provide: SpeakerAssigner,
      useValue: {
        assign: async () => undefined,
        createAndAssign: async () => undefined,
        clearOutcome: async () => undefined,
      },
    },
  ],
})
export class StyleguidePage {
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly prompt = inject(PromptService);
  private readonly live = inject(LiveService);

  // ---- live hub (ticket 07 dev aid) --------------------------------------------------------------
  readonly liveState = this.live.state;
  readonly liveLabel = this.live.statusLabel;
  readonly liveFamilies = LIVE_FAMILIES;
  readonly liveRows = computed<KeyValueRow[]>(() => {
    const latest = this.live.latest();
    return LIVE_FAMILIES.map((family: LiveFamily) => ({
      label: family,
      value: family in latest ? truncate(JSON.stringify(latest[family]), 240) : null,
      mono: true,
    }));
  });
  /** Snapshot-shaped signals (filled by GetSnapshot on every (re)connect, then by pushes). */
  readonly liveStateRows = computed<KeyValueRow[]>(() => {
    const rows: [string, unknown][] = [
      ['queue', this.live.queue()],
      ['attributionProgress', this.live.attributionProgress()],
      ['assembly', this.live.assembly()],
      ['voiceBatch', this.live.voiceBatch()],
      ['watchdog', this.live.watchdog()],
      ['throughput', this.live.throughput()],
      ['projects', Object.keys(this.live.projects())],
    ];
    return rows.map(([label, value]) => ({
      label,
      value: value === null ? null : truncate(JSON.stringify(value), 240),
      mono: true,
    }));
  });
  readonly liveStatusKind = computed<StatusKind>(() => {
    switch (this.liveState()) {
      case 'connected':
        return 'ok';
      case 'disconnected':
        return 'error';
      default:
        return 'warn';
    }
  });

  // ---- speakers ----------------------------------------------------------------------------------
  readonly speakerStates: SpeakerChipState[] = [
    'named',
    'unknown',
    'mixed',
    'narration',
    'narrator-linked',
  ];
  readonly roster: SpeakerRosterEntry[] = [
    { id: 'narr', name: 'Narrator', isNarrator: true },
    { id: 'hardin', name: 'Hardin', aliases: ['Salvor Hardin', 'the Mayor'] },
    { id: 'pirenne', name: 'Pirenne', aliases: ['Lewis Pirenne'] },
    { id: 'seldon', name: 'Hari Seldon' },
    { id: 'gaal', name: 'Gaal Dornick' },
    { id: 'anselm', name: 'Anselm haut Rodric' },
  ];
  readonly pickedSpeaker = signal<string | null>('hardin');

  onPick(id: string): void {
    this.pickedSpeaker.set(id);
    this.toast.info(`pick: ${id}`);
  }

  // ---- dialogs -----------------------------------------------------------------------------------
  readonly dialogResult = signal('');

  async openConfirm(destructive: boolean): Promise<void> {
    const ok = await this.confirm.confirm({
      title: destructive ? 'Delete project?' : 'Reread book?',
      message: destructive
        ? 'This removes the project folder, its database and all generated audio.'
        : 'Existing attribution and audio are kept where the paragraphs still match.',
      destructive,
      confirmLabel: destructive ? 'Delete' : 'Reread',
    });
    this.dialogResult.set(`confirm → ${ok}`);
  }

  async openPrompt(multiline: boolean): Promise<void> {
    const text = await this.prompt.text({
      title: multiline ? 'Edit paragraph text' : 'Rename chapter',
      label: multiline ? 'Text' : 'Title',
      initial: multiline ? 'Hardin leaned back in his chair.' : 'Chapter 3',
      multiline,
      required: true,
    });
    this.dialogResult.set(`prompt → ${text === null ? 'null' : JSON.stringify(text)}`);
  }

  // ---- audio -------------------------------------------------------------------------------------
  readonly toneA = tone(440, 2);
  readonly toneB = tone(330, 2);

  readonly schemeChoice = signal<SchemeChoice>('both');
  readonly schemes = () =>
    this.schemeChoice() === 'both'
      ? (['light', 'dark'] as const)
      : ([this.schemeChoice()] as Scheme[]);

  readonly toc: TocEntry[] = [
    { anchor: 'tokens', label: 'Tokens' },
    { anchor: 'page-header', label: 'Page header' },
    { anchor: 'empty-state', label: 'Empty state' },
    { anchor: 'status-chip', label: 'Status chip' },
    { anchor: 'count-badge', label: 'Count badge' },
    { anchor: 'key-value', label: 'Key/value' },
    { anchor: 'inline-edit', label: 'Inline edit' },
    { anchor: 'file-drop', label: 'File drop' },
    { anchor: 'project-card', label: 'Project card' },
    { anchor: 'reader-rows', label: 'Reader paragraph' },
    { anchor: 'sparkline', label: 'Sparkline' },
    { anchor: 'throughput', label: 'Throughput' },
    { anchor: 'toast', label: 'Toast' },
    { anchor: 'job-pill', label: 'Job pill' },
    { anchor: 'job-card', label: 'Job card' },
    { anchor: 'config-list', label: 'Config list' },
    { anchor: 'config-editor-frame', label: 'Config editor frame' },
    { anchor: 'settings-form', label: 'Settings form' },
    { anchor: 'docker-controls', label: 'Docker controls' },
    { anchor: 'preflight-sheet', label: 'Preflight sheet' },
    { anchor: 'stream-llm', label: 'LLM stream' },
    { anchor: 'stream-audio', label: 'Audio stream' },
    { anchor: 'speaker-chip', label: 'Speaker chip' },
    { anchor: 'speaker-menu', label: 'Speaker menu' },
    { anchor: 'dialogs', label: 'Dialogs' },
    { anchor: 'audio-player', label: 'Audio player' },
    { anchor: 'live', label: 'Live' },
  ];

  // ---- status / badges ---------------------------------------------------------------------------
  readonly statuses: StatusKind[] = ['ok', 'warn', 'error', 'info', 'busy', 'neutral'];

  // ---- key value ---------------------------------------------------------------------------------
  readonly kvRows: KeyValueRow[] = [
    { label: 'Title', value: 'Foundation' },
    { label: 'Author', value: 'Isaac Asimov' },
    { label: 'Source file', value: 'foundation.epub', mono: true },
    { label: 'Items', value: 3415 },
    { label: 'Narrator', value: null },
  ];

  // ---- inline edit / file drop -------------------------------------------------------------------
  readonly inlineValue = signal('Chapter 3 — The Encyclopedists');
  readonly shelfProjects: ProjectSummary[] = [
    {
      folderName: 'foundation',
      title: 'Foundation',
      author: 'Isaac Asimov',
      coverImage: null,
      audioItemTotal: 1240,
      audioItemDone: 310,
      audioPercent: 25,
      fileType: 'Epub',
    },
    {
      folderName: 'the-left-hand-of-darkness',
      title: 'The Left Hand of Darkness: a very long title that wraps onto two lines',
      author: 'Ursula K. Le Guin',
      coverImage: null,
      audioItemTotal: 0,
      audioItemDone: 0,
      audioPercent: 0,
      fileType: 'Text',
    },
  ];
  readonly droppedFiles = signal<string[]>([]);
  readonly rejectedFiles = signal<string[]>([]);

  onInlineSave(value: string): void {
    this.inlineValue.set(value);
    this.toast.success(`Saved "${value}"`);
  }

  onFiles(files: File[]): void {
    this.droppedFiles.set(files.map((f) => `${f.name} (${Math.round(f.size / 1024)} kB)`));
  }

  onRejected(rejected: RejectedFile[]): void {
    this.rejectedFiles.set(rejected.map((r) => `${r.file.name}: ${r.reason}`));
  }

  // ---- sparkline ---------------------------------------------------------------------------------
  readonly sparkValues = [
    12, 14, 13, 18, 22, 21, 25, 24, 28, 30, 27, 31, 29, 33, 35, 34, 36, 38, 37, 40,
  ];
  readonly flatValues = [5, 5, 5, 5, 5];

  // ---- throughput --------------------------------------------------------------------------------
  readonly throughputRunning: ThroughputSnapshot = {
    hasRun: true,
    isRunActive: true,
    runThroughput: null,
    generationRate: 38.4,
    generationRateHistory: this.sparkValues,
    perConfig: [],
  };
  readonly throughputEnded: ThroughputSnapshot = {
    hasRun: true,
    isRunActive: false,
    runThroughput: 31.2,
    generationRate: null,
    generationRateHistory: this.sparkValues,
    perConfig: [
      {
        configId: 1,
        configName: 'gemma-4b',
        requests: 42,
        tokensOut: 5120,
        generationMs: 148000,
        tokensPerSecond: 34.6,
      },
      {
        configId: 2,
        configName: 'gemma-26b (thinking)',
        requests: 6,
        tokensOut: 2210,
        generationMs: 86000,
        tokensPerSecond: 25.7,
      },
    ],
  };

  // ---- toasts ------------------------------------------------------------------------------------
  /** A book mid-production: imported and cast, attribution running, voices and audio outstanding. */
  readonly pipelineSteps = derivePipeline({
    status: {
      hasContent: true,
      characters: 14,
      charactersWithLines: 11,
      readyVoices: 7,
      items: { total: 1240, withAudio: 310, unattributed: 96 },
      attribution: { remaining: 38, processing: true, queued: 12 },
      audio: { remaining: 402 },
      review: 3,
      volumeIds: ['v1'],
      nodes: {},
      revision: 42,
    },
    nodes: {
      v1: {
        attributionRemaining: 38,
        audioRemaining: 402,
        review: 3,
        attributionProcessing: true,
        attributionQueued: 12,
        isDone: false,
      },
    },
    folderAudioRemaining: 402,
    audioInFlight: 0,
    voiceBatchRunning: false,
    assemblyRunning: false,
    lastBuild: null,
  });
  readonly pipelineBusy = signal<string | null>(null);

  // ---- reader rows -------------------------------------------------------------------------------
  readonly readerMode = signal<ReaderMode>('speakers');
  readonly readerParagraphs: ParagraphDto[] = [
    readerParagraph('sg-p1', [
      readerItem('sg-n1', 'Narration', 'Hardin leaned back in his chair.', 'sg-narrator'),
    ]),
    readerParagraph('sg-p2', [
      readerItem(
        'sg-d1',
        'Character',
        '“The Encyclopedia comes first,”',
        'sg-pirenne',
        'audio/x.wav',
      ),
      readerItem('sg-n2', 'Narration', 'said Pirenne.', 'sg-narrator'),
    ]),
    readerParagraph('sg-p3', [
      readerItem('sg-d2', 'Character', '“Then you’ll have to face the Anacreonians.”', null),
    ]),
  ];
  readonly readerCtx = computed<RowContext>(() => ({
    folder: 'styleguide',
    mode: this.readerMode(),
    speakers: {
      names: { 'sg-pirenne': 'Pirenne' },
      narrator: { characterId: 'sg-narrator', displayName: 'Narrator', isLinked: false },
    },
    paragraphStatus: { 'sg-p3': { status: 'Queued' } },
    itemStatus: {
      'sg-d1': { audioVersion: 2 },
      'sg-d2': { outcome: { kind: 'Failed', reason: 'TTS timed out' } },
    },
    voices: {
      'sg-n1': { voiceName: 'Deep', narratedBy: null },
      'sg-n2': { voiceName: 'Deep', narratedBy: null },
      'sg-d1': { voiceName: 'Pirenne — Dry', narratedBy: null },
      'sg-d2': { voiceName: null, narratedBy: null },
    },
    reviews: {
      'sg-d1': {
        state: 'NeedsReview',
        normalizeOk: true,
        normalizeReason: null,
        verifyOk: false,
        wer: 0.31,
        verifyReason: 'WER above threshold',
        transcript: null,
        originalTextSnapshot: null,
      },
    },
    selectable: this.readerMode() !== 'audio',
    selected: new Set(['sg-p2']),
    itemSelectable: this.readerMode() === 'audio',
    selectedItems: new Set(),
    narratorOnlyMode: false,
    ancestry: {},
    roster: [
      { id: 'sg-narrator', name: 'Narrator', isNarrator: true },
      { id: 'sg-pirenne', name: 'Pirenne' },
      { id: 'sg-hardin', name: 'Hardin', aliases: ['Salvor'] },
    ],
  }));

  onPipeline(event: PipelineActionEvent): void {
    this.toast.info(`${event.step}: ${event.action}`);
  }

  onShelf(action: string, project: ProjectSummary): void {
    this.toast.info(`${action}: ${project.title}`);
  }

  toastSuccess(): void {
    this.toast.success('Assembly finished: foundation.m4b');
  }
  toastInfo(): void {
    this.toast.info('Book updated elsewhere — refreshing');
  }
  toastWarn(): void {
    this.toast.warn('Voice batch complete: 12 done, 1 failed');
  }
  toastError(): void {
    this.toast.problem({ status: 409, title: 'Conflict', detail: '18 items still need audio.' });
  }

  // ---- jobs --------------------------------------------------------------------------------------
  readonly jobs: JobView[] = [
    {
      id: 'attr',
      kind: 'attribution',
      label: 'Attributing',
      state: 'running',
      queued: 12,
      processing: 1,
      completed: 88,
      total: 101,
      etaSeconds: 38,
      elapsedSeconds: 271,
      rate: '3.1 s/para',
      cancellable: true,
    },
    {
      id: 'audio',
      kind: 'audio',
      label: 'Generating audio',
      state: 'queued',
      queued: 240,
      total: 240,
      cancellable: true,
    },
    {
      id: 'batch',
      kind: 'voiceBatch',
      label: 'Voice prompts',
      state: 'done',
      completed: 14,
      total: 14,
      dismissible: true,
    },
    {
      id: 'asm',
      kind: 'assembly',
      label: 'Assembling',
      state: 'failed',
      error: 'ffmpeg exited with code 1: unsupported codec',
      dismissible: true,
    },
    {
      id: 'wd',
      kind: 'watchdog',
      label: 'Recovering llama',
      state: 'running',
      detail: 'restart 2 of 3',
    },
  ];
  readonly jobHistory = this.sparkValues;

  onJob(action: string, id: string): void {
    this.toast.info(`${action}: ${id}`);
  }

  // ---- config list / editor ----------------------------------------------------------------------
  readonly configItems: ConfigListItem[] = [
    { id: 'c1', name: 'Gemma 26B', subtitle: 'registry-llama · gemma-26b', isActive: true },
    { id: 'c2', name: 'Qwen 28B', subtitle: 'registry-llama · qwen-28b', badge: 'thinking' },
    { id: 'c3', name: 'Gemma 4B (fast)', subtitle: 'registry-llama · gemma-4b' },
  ];
  readonly selectedConfig = signal<string | null>('c1');
  readonly editorDirty = signal(false);
  readonly editorSaving = signal(false);

  saveEditor(): void {
    this.editorSaving.set(true);
    setTimeout(() => {
      this.editorSaving.set(false);
      this.editorDirty.set(false);
      this.toast.success('Saved');
    }, 800);
  }

  // ---- settings form -----------------------------------------------------------------------------
  readonly ttsSchema: SettingsSchema = {
    type: 'VoxCpm2',
    fields: [
      {
        key: 'cfgValue',
        label: 'CFG value',
        kind: 'number',
        min: 1,
        max: 4,
        step: 0.1,
        default: 2,
        help: 'Guidance strength',
      },
      {
        key: 'inferenceTimesteps',
        label: 'Inference timesteps',
        kind: 'number',
        min: 4,
        max: 30,
        step: 1,
        default: 10,
      },
      { key: 'maxLength', label: 'Max length', kind: 'number', default: 2000 },
      { key: 'normalize', label: 'Normalize text', kind: 'boolean', default: true },
      { key: 'denoise', label: 'Denoise prompt', kind: 'boolean', default: false },
      {
        key: 'language',
        label: 'Language',
        kind: 'enum',
        options: [
          { value: 'en', label: 'English' },
          { value: 'de', label: 'German' },
        ],
        default: 'en',
      },
      { key: 'model', label: 'Model', kind: 'string', default: 'voxcpm2-base' },
      { key: 'systemPrompt', label: 'System prompt', kind: 'text', default: '', help: 'Optional' },
    ],
  };
  readonly defaultsValues = signal<SettingsValues>({
    cfgValue: 2.5,
    normalize: true,
    language: 'en',
  });
  readonly overrideValues = signal<SettingsValues>({ cfgValue: 3, denoise: true });
  readonly overrideValid = signal(true);

  // ---- docker / preflight ------------------------------------------------------------------------
  readonly dockerStatuses: AiServiceStatus[] = [
    'Ready',
    'Starting',
    'Stopped',
    'Recovering',
    'Down',
    'NotFound',
    'Unknown',
  ];

  readonly preflightPlan: PreflightPlan = {
    ready: false,
    toStart: [{ name: 'llama', status: 'Stopped' }],
    conflicts: [{ name: 'chatterbox', reason: 'Holds the GPU; one model at a time' }],
  };
  readonly preflightProgress: PreflightServiceProgress[] = [
    { name: 'chatterbox', stage: 'stopped' },
    { name: 'llama', stage: 'starting' },
  ];
  readonly preflightFailed: PreflightServiceProgress[] = [
    { name: 'chatterbox', stage: 'stopped' },
    { name: 'llama', stage: 'failed', error: 'health endpoint did not answer within 300 s' },
  ];

  // ---- streams -----------------------------------------------------------------------------------
  readonly llmEvents: LlmStreamEvent[] = [
    { kind: 'runStarted' },
    {
      kind: 'requestStarted',
      paragraphPreview: '"The Encyclopedia comes first," said Pirenne.',
      prompt: 'You are attributing dialog…\n\n{"items":[…]}',
      configId: 'c1',
      configName: 'Gemma 26B',
    },
    { kind: 'thinkingDelta', text: 'Pirenne is named in the tag. ' },
    { kind: 'contentDelta', text: '{"speaker":"Pirenne"}' },
    {
      kind: 'streamCompleted',
      tokensIn: 812,
      tokensOut: 14,
      generationMs: 620,
      tokensPerSecond: 22.6,
    },
    { kind: 'escalationStarted', step: 2, configName: 'Qwen 28B', itemCount: 3 },
    {
      kind: 'requestStarted',
      paragraphPreview: '"Then you\'ll have to face the Anacreonians…"',
      prompt: '…',
      configId: 'c2',
      configName: 'Qwen 28B',
    },
    { kind: 'contentDelta', text: '{"speaker":"Har' },
    {
      kind: 'requestStarted',
      paragraphPreview: 'Hardin leaned back in his chair.',
      prompt: '…',
      configId: 'c2',
      configName: 'Qwen 28B',
    },
    { kind: 'streamFailed', reason: 'model still loading' },
  ];

  readonly audioEvents: AudioGenEvent[] = [
    {
      kind: 'itemStarted',
      id: 'i1',
      attempt: 1,
      character: 'Pirenne',
      text: '"The Encyclopedia comes first," said Pirenne.',
    },
    { kind: 'audioGenerated', id: 'i1', attempt: 1 },
    { kind: 'normalized', id: 'i1', attempt: 1, ok: true },
    { kind: 'postProcessed', id: 'i1', attempt: 1, stepId: 'silence-trim', applied: true },
    {
      kind: 'postProcessed',
      id: 'i1',
      attempt: 1,
      stepId: 'denoise',
      applied: false,
      reason: 'disabled',
    },
    {
      kind: 'transcribed',
      id: 'i1',
      attempt: 1,
      transcript: 'The encyclopedia comes first, said Pirenne.',
    },
    { kind: 'verified', id: 'i1', attempt: 1, ok: true, wer: 0.04, rescued: false },
    {
      kind: 'itemStarted',
      id: 'i2',
      attempt: 2,
      character: 'Hardin',
      text: 'Hardin leaned back in his chair.',
    },
    { kind: 'audioGenerated', id: 'i2', attempt: 2 },
    { kind: 'normalized', id: 'i2', attempt: 2, ok: false, reason: 'clipping' },
    {
      kind: 'itemStarted',
      id: 'i3',
      attempt: 1,
      character: 'Narration',
      text: 'It was a long silence.',
    },
    { kind: 'failed', id: 'i3', attempt: 1, reason: 'TTS returned 422: empty transcript' },
  ];
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

/** Tiny generated WAV (mono 8 kHz sine) as a data URL, so the audio player story works offline. */
function tone(frequency: number, seconds: number): string {
  const rate = 8000;
  const samples = rate * seconds;
  const buffer = new ArrayBuffer(44 + samples * 2);
  const view = new DataView(buffer);
  const write = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
  };
  write(0, 'RIFF');
  view.setUint32(4, 36 + samples * 2, true);
  write(8, 'WAVE');
  write(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  write(36, 'data');
  view.setUint32(40, samples * 2, true);
  for (let i = 0; i < samples; i++) {
    const fade = Math.min(1, i / 400, (samples - i) / 400);
    view.setInt16(44 + i * 2, Math.sin((2 * Math.PI * frequency * i) / rate) * 12000 * fade, true);
  }
  let binary = '';
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return `data:audio/wav;base64,${btoa(binary)}`;
}

function readerItem(
  id: string,
  itemType: 'Narration' | 'Character',
  text: string,
  characterId: string | null,
  audioFileName: string | null = null,
): ParagraphItemDto {
  return {
    id,
    itemType,
    text,
    characterId,
    audioFileName,
    voiceInstructions: null,
    orderKey: id,
    isPause: false,
  };
}

function readerParagraph(id: string, items: ParagraphItemDto[]): ParagraphDto {
  return { id, items, isPauseParagraph: false };
}
