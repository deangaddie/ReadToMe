import { describe, expect, it } from 'bun:test';
import { shellContext } from '@app/route-meta';
import type { RouteDef } from '@app/core/router';
import { GLOBAL_NAV_ITEMS, type NavItem, contextNavItems, isActive } from './shell-nav';

describe('shell-nav', () => {
  it('lists the project sections for a project route', () => {
    const items = contextNavItems({ section: 'project', title: 'Book', folder: 'foundation' });
    expect(items.map((i) => i.label)).toEqual(['Overview', 'Book', 'Cast', 'Export']);
    expect(items[1]?.link).toBe('projects/foundation/book');
    expect(items[0]?.exact).toBe(true);
  });

  it('encodes the folder in project links', () => {
    const items = contextNavItems({ section: 'project', title: 'Book', folder: 'my book' });
    expect(items[0]?.link).toBe('projects/my%20book');
  });

  it('lists the settings pages for a settings route', () => {
    const items = contextNavItems({ section: 'settings', title: 'LLM' });
    expect(items.map((i) => i.link.split('/').at(-1))).toEqual([
      'llm',
      'prompts',
      'tts',
      'voice-design',
      'transcription',
      'similarity',
      'audio',
      'services',
      'themes',
    ]);
  });

  it('has no context group on the shelf', () => {
    expect(contextNavItems({ section: 'projects', title: 'Projects' })).toEqual([]);
  });

  it('offers Projects and Settings globally, not the pruned style guide', () => {
    expect(GLOBAL_NAV_ITEMS.map((i) => i.label)).toEqual(['Projects', 'Settings']);
  });

  it('marks an item active on its path, or below it unless exact', () => {
    const overview = {
      label: 'Overview',
      icon: 'dashboard',
      link: 'projects/dune',
      exact: true,
    } satisfies NavItem;
    const book = { label: 'Book', icon: 'menu_book', link: 'projects/dune/book' } satisfies NavItem;
    const settings = { label: 'Settings', icon: 'settings', link: 'settings' } satisfies NavItem;
    expect(isActive(overview, 'projects/dune')).toBe(true);
    expect(isActive(overview, 'projects/dune/book')).toBe(false);
    expect(isActive(book, 'projects/dune/book')).toBe(true);
    expect(isActive(settings, 'settings/llm')).toBe(true);
    expect(isActive(settings, 'settingsx')).toBe(false);
  });
});

describe('shellContext', () => {
  const projects: RouteDef = { path: 'projects', title: 'Projects' };
  const folder: RouteDef = { path: ':folder', title: (p) => p['folder'] ?? '' };
  const settings: RouteDef = { path: 'settings', title: 'Settings' };

  it('reads the section from the first segment and the folder param', () => {
    expect(shellContext({ chain: [projects, { path: '' }], params: {} })).toEqual({
      section: 'projects',
      title: 'Projects',
    });
    expect(
      shellContext({
        chain: [projects, folder, { path: 'book', title: 'Book' }],
        params: { folder: 'dune' },
      }),
    ).toEqual({ section: 'project', title: 'Book', folder: 'dune' });
    expect(shellContext({ chain: [settings, { path: 'llm', title: 'LLM' }], params: {} })).toEqual({
      section: 'settings',
      title: 'LLM',
    });
  });

  it('titles the page by the deepest titled route', () => {
    expect(
      shellContext({
        chain: [projects, folder, { path: 'cast/:characterId', title: 'Cast' }],
        params: { folder: 'dune', characterId: 'c1' },
      }).title,
    ).toBe('Cast');
    expect(shellContext({ chain: [projects, folder], params: { folder: 'dune' } }).title).toBe(
      'dune',
    );
  });

  it('is the shelf when nothing matched', () => {
    expect(shellContext(null)).toEqual({ section: 'projects', title: '' });
  });
});
