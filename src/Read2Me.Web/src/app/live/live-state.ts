/**
 * Pure reducers that fold hub messages into the snapshot-shaped signals `LiveService` exposes, so
 * a view reads "the assembly state" rather than replaying events. Each mirrors what the server's
 * snapshot would say after the same event (LiveRelay.BuildSnapshot).
 */

export const IDLE_ASSEMBLY: AssemblyState = {
  isRunning: false,
  currentPhase: null,
  encodePercent: 0,
  lastError: null,
  audioRemainingCount: 0,
  folder: null,
  outputFileName: null,
};

export const IDLE_VOICE_BATCH: VoiceBatchState = {
  isRunning: false,
  processed: 0,
  total: 0,
  failed: 0,
  currentVoiceName: null,
  currentOperation: null,
  lastError: null,
};

export function applyAssembly(state: AssemblyState, m: AssemblyMessage): AssemblyState {
  // Every kind names the run's project; a host that does not leaves what is known alone.
  const folder = m.folder ?? state.folder ?? null;
  switch (m.kind) {
    case 'PhaseStarted':
      return {
        ...state,
        folder,
        isRunning: true,
        currentPhase: m.phase ?? null,
        lastError: null,
        outputFileName: null,
        encodePercent: m.phase === 'Encode' ? 0 : state.encodePercent,
      };
    case 'Progress':
      return { ...state, folder, isRunning: true, encodePercent: (m.fraction ?? 0) * 100 };
    case 'Completed':
      return {
        ...state,
        folder,
        isRunning: false,
        currentPhase: null,
        encodePercent: 100,
        outputFileName: m.outputFileName ?? null,
      };
    case 'Failed':
      return {
        ...state,
        folder,
        isRunning: false,
        currentPhase: null,
        lastError: m.reason ?? 'failed',
        outputFileName: null,
      };
    case 'Cancelled':
      return { ...state, folder, isRunning: false, currentPhase: null, outputFileName: null };
  }
}

export function applyVoiceBatch(state: VoiceBatchState, m: VoiceBatchMessage): VoiceBatchState {
  switch (m.kind) {
    case 'Started':
      return {
        isRunning: true,
        processed: 0,
        total: m.total ?? 0,
        failed: 0,
        currentVoiceName: null,
        currentOperation: m.operation ?? null,
        lastError: null,
      };
    case 'Progress':
      return {
        ...state,
        isRunning: true,
        processed: m.processed ?? state.processed,
        total: m.total ?? state.total,
        failed: m.failed ?? state.failed,
        currentVoiceName: m.currentVoiceName ?? null,
      };
    case 'VoiceUpdated':
      return state;
    case 'Completed':
      return {
        ...state,
        isRunning: false,
        processed: m.processed ?? state.processed,
        failed: m.failed ?? state.failed,
        currentVoiceName: null,
      };
    case 'Cancelled':
      return { ...state, isRunning: false, currentVoiceName: null };
  }
}

/** Merge a delta map into a status map: a `null` value removes the key. */
function mergeDeltas<T>(
  current: Record<string, T | null>,
  deltas: Record<string, T | null>,
): Record<string, T | null> {
  const next = { ...current };
  for (const [key, value] of Object.entries(deltas)) {
    if (value === null) delete next[key];
    else next[key] = value;
  }
  return next;
}

export function emptyProject(folder: string): ProjectSnapshot {
  return {
    folder,
    revision: 0,
    nodes: {},
    folderAudioRemaining: 0,
    paragraphs: {},
    items: {},
  };
}

export function applyNodeStatus(project: ProjectSnapshot, m: NodeStatusMessage): ProjectSnapshot {
  return {
    ...project,
    nodes: mergeDeltas(project.nodes, m.nodes),
    folderAudioRemaining: m.folderAudioRemaining,
  };
}

export function applyItemStatus(project: ProjectSnapshot, m: ItemStatusMessage): ProjectSnapshot {
  return {
    ...project,
    paragraphs: mergeDeltas(project.paragraphs, m.paragraphs),
    items: mergeDeltas(project.items, m.items),
  };
}
import type {
  AssemblyMessage,
  AssemblyState,
  ItemStatusMessage,
  NodeStatusMessage,
  ProjectSnapshot,
  VoiceBatchMessage,
  VoiceBatchState,
} from '@app/api';
