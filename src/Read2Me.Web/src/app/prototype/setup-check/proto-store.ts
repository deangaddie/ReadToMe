/**
 * PROTOTYPE — throwaway (wayfinder ticket "Setup check: where it lives and first-load surfacing").
 * Fake Setup check state: no API, no hub. Rows, scenarios and the Full check are simulated in
 * memory with timers so the four UI variants can be judged against the real shell.
 */
import { computed, signal } from '@app/core/signals';

export type Result = 'passed' | 'failed' | 'notConfirmed' | 'notRun' | 'notUsed' | 'running';
export type Group = 'Workspace and tools' | 'AI configs' | 'Docker services';
export type Scenario = 'firstRun' | 'healthy' | 'failing' | 'noWorkspace';
export type Variant = 'A' | 'B' | 'C' | 'D';

export const VARIANTS: Record<Variant, string> = {
  A: 'Settings page + start banner',
  B: 'Projects home panel',
  C: 'Top-level Setup + first-run gate',
  D: 'Activity bar job + drawer',
};

export const SCENARIOS: Record<Scenario, string> = {
  firstRun: 'First run ever (fresh app.db)',
  healthy: 'Healthy start',
  failing: 'Start with failures',
  noWorkspace: 'No Workspace',
};

export interface Fix {
  text: string;
  href?: string;
  linkLabel?: string;
  command?: string;
}

export interface Row {
  id: string;
  group: Group;
  name: string;
  tier: 'quick' | 'full';
  result: Result;
  expected: string;
  found: string;
  fix: Fix;
  note?: string;
  managed?: string;
}

const ws = 'D:\\Read\\WS';
const localJson = 'D:\\Dev\\Repos\\deangaddie\\ReadToMe\\src\\Read2Me.App\\appsettings.Local.json';

function baseRows(): Row[] {
  return [
    {
      id: 'workspace',
      group: 'Workspace and tools',
      name: 'Workspace',
      tier: 'quick',
      result: 'passed',
      expected: 'An absolute, writable folder (Workspace:FolderPath)',
      found: `${ws} — app.db present`,
      fix: { text: `Set Workspace:FolderPath in ${localJson}, then restart the app.`, command: localJson },
    },
    {
      id: 'ffmpeg',
      group: 'Workspace and tools',
      name: 'ffmpeg',
      tier: 'full',
      result: 'passed',
      expected: 'ffmpeg -version from D:\\Dev\\ffmpeg\\bin\\ffmpeg.exe',
      found: 'ffmpeg 7.1 — tone encoded to .m4b with adynEQ + alimiter',
      fix: { text: 'Set the ffmpeg path.', href: 'settings/audio', linkLabel: 'Settings → Audio processing' },
    },
    {
      id: 'docker',
      group: 'Workspace and tools',
      name: 'docker CLI',
      tier: 'quick',
      result: 'passed',
      expected: 'docker on PATH',
      found: 'Docker 28.4.0',
      fix: { text: 'Install Docker Desktop and put docker on PATH, then restart the app.' },
    },
    {
      id: 'llm',
      group: 'AI configs',
      name: 'LLM — Attribution (qwen-28b)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'qwen-28b listed by GET /v1/models; 1-token completion',
      found: 'read2me-llama stopped — run the Full check',
      fix: { text: 'Choose the model or URL.', href: 'settings/llm', linkLabel: 'Settings → LLM' },
    },
    {
      id: 'llm-chain',
      group: 'AI configs',
      name: 'LLM — chain step Thinking (qwen-28b)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'qwen-28b, thinking on',
      found: 'read2me-llama stopped — run the Full check',
      fix: { text: 'Edit the escalation chain.', href: 'settings/llm', linkLabel: 'Settings → LLM' },
    },
    {
      id: 'tts',
      group: 'AI configs',
      name: 'Paragraph TTS (voxcpm2)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'Speech from the sample clip, duration > 0',
      found: 'read2me-audiocpp stopped — run the Full check',
      fix: { text: 'Choose the TTS config.', href: 'settings/tts', linkLabel: 'Settings → Paragraph TTS' },
    },
    {
      id: 'voice-design',
      group: 'AI configs',
      name: 'Voice design (qwen3-voice-design)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'Audio from a fixed prompt, duration > 0',
      found: 'read2me-audiocpp stopped — run the Full check',
      fix: { text: 'Choose the voice design config.', href: 'settings/voice-design', linkLabel: 'Settings → Voice design' },
    },
    {
      id: 'transcription',
      group: 'AI configs',
      name: 'Transcription (whisper base.en)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'Non-empty transcript of the sample clip',
      found: 'read2me-whisper stopped — run the Full check',
      fix: { text: 'Choose the transcription config.', href: 'settings/transcription', linkLabel: 'Settings → Transcription' },
    },
    {
      id: 'similarity',
      group: 'AI configs',
      name: 'Similarity (MiniLM-L6)',
      tier: 'full',
      result: 'notConfirmed',
      expected: 'Score ≥ 0.80 against the clip text',
      found: 'read2me-minilm-l6 stopped — run the Full check',
      fix: { text: 'Choose the similarity config.', href: 'settings/similarity', linkLabel: 'Settings → Similarity' },
    },
    ...(
      [
        ['read2me-llama', 'llama', 'GPU'],
        ['read2me-audiocpp', 'audiocpp', 'GPU'],
        ['read2me-whisper', 'whisper', 'CPU'],
        ['read2me-minilm-l6', 'minilm-l6', 'CPU'],
      ] as const
    ).map(
      ([container, service, kind]): Row => ({
        id: `svc-${service}`,
        group: 'Docker services',
        name: `${container} (${kind})`,
        tier: 'quick',
        result: 'notConfirmed',
        expected: 'Container exists; health probe answers',
        found: 'Stopped',
        fix: { text: 'Start it from the AI services page, or run the Full check.', href: 'settings/services', linkLabel: 'Settings → AI services' },
      }),
    ),
    {
      id: 'svc-mpnet-base-v2',
      group: 'Docker services',
      name: 'read2me-mpnet-base-v2 (CPU)',
      tier: 'quick',
      result: 'notUsed',
      expected: '—',
      found: 'No active config uses it',
      fix: { text: 'Nothing to do.' },
    },
  ];
}

function scenarioRows(s: Scenario): Row[] {
  const rows = baseRows();
  const set = (id: string, patch: Partial<Row>) => {
    const r = rows.find((x) => x.id === id);
    if (r) Object.assign(r, patch);
  };
  if (s === 'firstRun') {
    set('ffmpeg', {
      result: 'failed',
      found: "'ffmpeg' not found on PATH (path setting is blank)",
      fix: { text: 'Set the full path to ffmpeg.exe.', href: 'settings/audio', linkLabel: 'Settings → Audio processing' },
    });
    set('transcription', { result: 'notUsed', found: 'No active transcription config', note: 'Audio is generated without an accuracy score.' });
    set('similarity', { result: 'notUsed', found: 'No active similarity config', note: 'Audio is generated without an accuracy score.' });
    set('svc-whisper', { result: 'notUsed', found: 'No active config uses it' });
    set('svc-minilm-l6', { result: 'notUsed', found: 'No active config uses it' });
  }
  if (s === 'healthy') {
    set('svc-llama', { result: 'passed', found: 'Ready — /health 200, qwen-28b loaded' });
    set('llm', { result: 'passed', found: 'qwen-28b listed (loaded)' });
    set('llm-chain', { result: 'passed', found: 'qwen-28b listed (loaded)' });
    set('ffmpeg', { note: 'appsettings says C:\\tools\\ffmpeg.exe; the app uses D:\\Dev\\ffmpeg\\bin\\ffmpeg.exe — change it in Settings → Audio processing.' });
  }
  if (s === 'failing') {
    set('svc-llama', { result: 'passed', found: 'Ready — /health 200' });
    set('llm', {
      result: 'failed',
      found: "GET /v1/models lists qwen-27b, gemma-12b — no 'qwen-28b'",
      fix: { text: 'Add the preset to models.ini and restart llama, or pick a listed model.', href: 'settings/llm', linkLabel: 'Settings → LLM', command: 'Infra/llama/config/models.ini' },
    });
    set('llm-chain', { result: 'failed', found: "No 'qwen-28b' preset", fix: { text: 'Same preset as above.', command: 'Infra/llama/config/models.ini' } });
    set('svc-whisper', {
      result: 'failed',
      found: 'NotFound — no container named read2me-whisper',
      fix: { text: 'Create it from Infra/.', command: 'docker compose up -d whisper' },
    });
    set('transcription', { result: 'notRun', found: 'Blocked: read2me-whisper not found' });
  }
  if (s === 'noWorkspace') {
    for (const r of rows) {
      if (r.id === 'workspace') continue;
      r.result = 'notRun';
      r.found = 'Needs the Workspace';
      delete r.note;
    }
    set('workspace', {
      result: 'failed',
      expected: 'An absolute, writable folder',
      found: "Workspace:FolderPath is empty (winning source: appsettings.Local.json — file doesn't exist)",
      fix: {
        text: `Copy appsettings.Local.example.json to appsettings.Local.json, set Workspace:FolderPath to an absolute folder, then restart the app.`,
        command: localJson,
      },
    });
  }
  return rows;
}

// ---- state --------------------------------------------------------------------------------------

function readVariant(): Variant {
  const fromUrl = new URLSearchParams(location.search).get('variant');
  const stored = (() => {
    try {
      return sessionStorage.getItem('proto-variant');
    } catch {
      return null;
    }
  })();
  const v = (fromUrl ?? stored ?? 'A') as Variant;
  return v in VARIANTS ? v : 'A';
}

export const variant = signal<Variant>(readVariant());
export const scenario = signal<Scenario>('failing');
export const rows = signal<Row[]>(scenarioRows('failing'));
export const quickAt = signal<Date>(new Date());
export const fullAt = signal<Date | null>(null);
/** Bumped by "Simulate app start": surfaces (banner, drawer, toast) reset their dismissal. */
export const startCount = signal(1);
export const dismissedAt = signal(0);
export const drawerOpen = signal(false);
export const panelExpanded = signal<boolean | null>(null);

export interface FullRun {
  step: number;
  total: number;
  label: string;
  lane: 'CPU' | 'GPU' | 'Local';
  service: string;
  cancelling: boolean;
}
export const full = signal<FullRun | null>(null);
export const restoreNote = signal<string | null>(null);

export const failedCount = computed(() => rows().filter((r) => r.result === 'failed').length);
export const noWorkspace = computed(() => scenario() === 'noWorkspace');
export const firstRun = computed(() => scenario() === 'firstRun');

export function setVariant(v: Variant): void {
  variant.set(v);
  try {
    sessionStorage.setItem('proto-variant', v);
  } catch {
    /* ignore */
  }
  const url = new URL(location.href);
  url.searchParams.set('variant', v);
  history.replaceState(history.state, '', url);
}

export function setScenario(s: Scenario): void {
  cancelTimers();
  full.set(null);
  restoreNote.set(null);
  scenario.set(s);
  rows.set(scenarioRows(s));
  fullAt.set(null);
  simulateStart();
}

export function simulateStart(): void {
  quickAt.set(new Date());
  startCount.update((n) => n + 1);
  panelExpanded.set(null);
  // Variant D opens its drawer once at start when something failed.
  drawerOpen.set(variant() === 'D' && failedCount() > 0);
}

export function dismiss(): void {
  dismissedAt.set(startCount());
}
export const dismissed = computed(() => dismissedAt() === startCount());

export function rerunQuick(): void {
  rows.set(scenarioRows(scenario()));
  quickAt.set(new Date());
}

// ---- Full check simulation ----------------------------------------------------------------------

interface Step {
  lane: FullRun['lane'];
  service: string;
  label: string;
  rows: string[];
}

const STEPS: Step[] = [
  { lane: 'Local', service: 'ffmpeg', label: 'Encoding a 1 s tone to .m4b', rows: ['ffmpeg'] },
  { lane: 'CPU', service: 'read2me-whisper', label: 'Starting read2me-whisper', rows: ['svc-whisper'] },
  { lane: 'CPU', service: 'read2me-whisper', label: 'Transcribing the sample clip', rows: ['transcription'] },
  { lane: 'CPU', service: 'read2me-minilm-l6', label: 'Starting read2me-minilm-l6 and scoring', rows: ['svc-minilm-l6', 'similarity'] },
  { lane: 'GPU', service: 'read2me-audiocpp', label: 'Stopping read2me-llama, starting read2me-audiocpp', rows: ['svc-audiocpp'] },
  { lane: 'GPU', service: 'read2me-audiocpp', label: 'Loading voxcpm2, speaking the sample sentence', rows: ['tts'] },
  { lane: 'GPU', service: 'read2me-audiocpp', label: 'Loading qwen3-voice-design', rows: ['voice-design'] },
  { lane: 'GPU', service: 'read2me-llama', label: 'Stopping read2me-audiocpp, starting read2me-llama', rows: ['svc-llama'] },
  { lane: 'GPU', service: 'read2me-llama', label: 'Loading qwen-28b, 1-token completion', rows: ['llm', 'llm-chain'] },
];

let timers: ReturnType<typeof setTimeout>[] = [];
let runRowIds: string[] = [];
function cancelTimers() {
  for (const t of timers) clearTimeout(t);
  timers = [];
}

function patchRows(ids: string[], fn: (r: Row) => Partial<Row>) {
  rows.update((all) => all.map((r) => (ids.includes(r.id) ? { ...r, ...fn(r) } : r)));
}

function outcome(r: Row): Partial<Row> {
  if (r.result === 'notUsed') return {};
  if (r.result === 'failed' && scenario() === 'failing') return {};
  if (r.result === 'failed' && r.id === 'ffmpeg') return {};
  if (scenario() === 'failing' && r.id === 'transcription') return { result: 'notRun' };
  const found: Record<string, string> = {
    'svc-whisper': 'Ready — started for the check, stopped again',
    'svc-minilm-l6': 'Ready — started for the check, stopped again',
    'svc-audiocpp': 'Ready — started for the check, stopped again',
    'svc-llama': 'Ready — qwen-28b reloaded',
    transcription: '"It was a bright cold day in April" (non-empty)',
    similarity: 'Score 0.93 ≥ 0.80',
    tts: 'voxcpm2 — 3.4 s of speech',
    'voice-design': 'qwen3-voice-design — 4.1 s of audio',
    llm: '200, content "Hi"',
    'llm-chain': '200, content "Hi"',
    ffmpeg: 'ffmpeg 7.1 — tone encoded to .m4b with adynEQ + alimiter',
  };
  return { result: 'passed', found: found[r.id] ?? r.found };
}

export function startFull(onlyRow?: string): void {
  const steps = onlyRow ? STEPS.filter((s) => s.rows.includes(onlyRow)) : STEPS;
  if (steps.length === 0) return;
  cancelTimers();
  restoreNote.set(null);
  runRowIds = steps.flatMap((x) => x.rows);
  const ms = 1400;
  steps.forEach((s, i) => {
    timers.push(
      setTimeout(() => {
        full.set({ step: i + 1, total: steps.length, label: s.label, lane: s.lane, service: s.service, cancelling: false });
        patchRows(s.rows, () => ({ result: 'running' }));
      }, i * ms),
    );
    timers.push(
      setTimeout(() => {
        const original = scenarioRows(scenario());
        patchRows(s.rows, (r) => {
          const o = original.find((x) => x.id === r.id) ?? r;
          return { result: o.result, found: o.found, ...outcome(o) };
        });
      }, i * ms + ms - 100),
    );
  });
  timers.push(
    setTimeout(() => {
      full.set(null);
      fullAt.set(new Date());
      restoreNote.set(
        scenario() === 'failing'
          ? 'Put back: read2me-llama running with qwen-27b loaded; read2me-whisper left as found (failed).'
          : 'Put back: CPU containers stopped, read2me-llama running with qwen-28b loaded.',
      );
    }, steps.length * ms),
  );
}

export function cancelFull(): void {
  const run = full();
  if (!run) return;
  cancelTimers();
  // The current step finishes; everything after it reads Not run.
  rows.update((all) =>
    all.map((r) =>
      r.result === 'running'
        ? { ...r, result: 'passed' as const, found: 'Finished before the cancel' }
        : runRowIds.includes(r.id) && r.result === 'notConfirmed'
          ? { ...r, result: 'notRun', found: 'Not run — Full check cancelled' }
          : r,
    ),
  );
  full.set(null);
  restoreNote.set('Cancelled after the current step. Put back: every container to its earlier state.');
}

export const fullEstimate = '≈ 2–3 min (5 model loads)';
