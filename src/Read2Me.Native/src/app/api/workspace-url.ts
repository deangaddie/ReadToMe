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
