/**
 * Pure reducers that fold hub messages into the snapshot-shaped signals `LiveService` exposes, so
 * a view reads "the assembly state" rather than replaying events. Each mirrors what the server's
 * snapshot would say after the same event (LiveRelay.BuildSnapshot).
 */

import {
  AssemblyMessage,
  AssemblyState,
  ItemStatusMessage,
  NodeStatusMessage,
  ProjectSnapshot,
  VoiceBatchMessage,
  VoiceBatchState,
} from './live-messages';

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
    case 'phaseStarted':
      return {
        ...state,
        folder,
        isRunning: true,
        currentPhase: m.phase ?? null,
        lastError: null,
        outputFileName: null,
        encodePercent: m.phase === 'Encode' ? 0 : state.encodePercent,
      };
    case 'progress':
      return { ...state, folder, isRunning: true, encodePercent: (m.fraction ?? 0) * 100 };
    case 'completed':
      return {
        ...state,
        folder,
        isRunning: false,
        currentPhase: null,
        encodePercent: 100,
        outputFileName: m.outputFileName ?? null,
      };
    case 'failed':
      return {
        ...state,
        folder,
        isRunning: false,
        currentPhase: null,
        lastError: m.reason ?? 'failed',
        outputFileName: null,
      };
    case 'cancelled':
      return { ...state, folder, isRunning: false, currentPhase: null, outputFileName: null };
  }
}

export function applyVoiceBatch(state: VoiceBatchState, m: VoiceBatchMessage): VoiceBatchState {
  switch (m.kind) {
    case 'started':
      return {
        isRunning: true,
        processed: 0,
        total: m.total ?? 0,
        failed: 0,
        currentVoiceName: null,
        currentOperation: m.operation ?? null,
        lastError: null,
      };
    case 'progress':
      return {
        ...state,
        isRunning: true,
        processed: m.processed ?? state.processed,
        total: m.total ?? state.total,
        failed: m.failed ?? state.failed,
        currentVoiceName: m.currentVoiceName ?? null,
      };
    case 'voiceUpdated':
      return state;
    case 'completed':
      return {
        ...state,
        isRunning: false,
        processed: m.processed ?? state.processed,
        failed: m.failed ?? state.failed,
        currentVoiceName: null,
      };
    case 'cancelled':
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
