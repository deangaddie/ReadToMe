import { breadcrumb, contextNavItems, toShellContext } from './shell-nav';

describe('shell-nav', () => {
  it('lists the project sections for a project route', () => {
    const items = contextNavItems({ section: 'project', title: 'Book', folder: 'foundation' });
    expect(items.map((i) => i.label)).toEqual(['Overview', 'Book', 'Cast', 'Export']);
    expect(items[1]?.link).toEqual(['/projects', 'foundation', 'book']);
    expect(items[0]?.exact).toBe(true);
  });

  it('lists the settings pages for a settings route', () => {
    const items = contextNavItems({ section: 'settings', title: 'LLM' });
    expect(items.map((i) => i.link.at(-1))).toEqual([
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

  it('builds project › section breadcrumbs, omitting the overview segment', () => {
    expect(
      breadcrumb({ section: 'project', title: 'Book', folder: 'foundation' }).map((c) => c.label),
    ).toEqual(['Projects', 'foundation', 'Book']);
    expect(
      breadcrumb({ section: 'project', title: 'Overview', folder: 'foundation' }).map(
        (c) => c.label,
      ),
    ).toEqual(['Projects', 'foundation']);
    expect(breadcrumb({ section: 'settings', title: 'Themes' }).map((c) => c.label)).toEqual([
      'Settings',
      'Themes',
    ]);
  });

  it('carries the folder param into the context only when present', () => {
    expect(toShellContext({ section: 'project', title: 'Book' }, { folder: 'dracula' })).toEqual({
      section: 'project',
      title: 'Book',
      folder: 'dracula',
    });
    expect(toShellContext({ section: 'projects', title: 'Projects' }, {})).toEqual({
      section: 'projects',
      title: 'Projects',
    });
  });
});
