import { withViewedStep } from './step-view';

/**
 * Media reads keep using the host's `/workspace/{folder}/…` static mount (spec D7). Item WAVs,
 * voice reference audio and cover images are addressed by the file name the API returns; `version`
 * (e.g. `AudioItemStatusDto.audioVersion`) becomes a `?v=` cache-buster so a regenerated file with
 * the same name is refetched.
 */
export function workspaceUrl(
  folder: string,
  fileName: string,
  version?: number | string | null,
): string {
  const path = fileName
    .split('/')
    .filter((segment) => segment.length > 0)
    .map(encodeURIComponent)
    .join('/');
  const base = `/workspace/${encodeURIComponent(folder)}/${path}`;
  return version === undefined || version === null || version === ''
    ? base
    : `${base}?v=${encodeURIComponent(String(version))}`;
}

/**
 * A state file — a voice's reference audio, the cover — as {@link workspaceUrl} names it, read at
 * the step the project is viewed at, if any (`?at=`, `StepFileReads.cs`), so past voices play and
 * past covers show. Item takes aren't Project state, so they keep {@link workspaceUrl}.
 */
export function stateFileUrl(
  folder: string,
  fileName: string,
  version?: number | string | null,
): string {
  return withViewedStep(workspaceUrl(folder, fileName, version), folder);
}
