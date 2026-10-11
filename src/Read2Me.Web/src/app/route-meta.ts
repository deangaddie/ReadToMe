import { type MatchedRoute, titleOf } from '@app/core/router';

/**
 * What the shell reads off the matched route chain (design §4, §5): which nav-rail context group to
 * show and the page title. Angular carried these as static route `data`; here they fall out of the
 * chain itself — the first segment names the section, the deepest titled route names the page.
 */
export type Section = 'projects' | 'project' | 'settings';

export interface ShellContext {
  section: Section;
  /** Page title: the deepest titled route in the chain. */
  title: string;
  folder?: string;
}

export function shellContext(match: MatchedRoute | null): ShellContext {
  if (!match) return { section: 'projects', title: '' };
  const { chain, params } = match;
  const folder = params['folder'];
  const section: Section =
    chain[0]?.path === 'settings' ? 'settings' : folder === undefined ? 'projects' : 'project';
  const title = chain.map((r) => titleOf(r, params)).findLast(Boolean) ?? '';
  return folder === undefined ? { section, title } : { section, title, folder };
}
