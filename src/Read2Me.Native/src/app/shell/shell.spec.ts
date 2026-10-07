import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { routes } from '@app/app.routes';
import { Router } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';

import { ProjectTitles } from '@app/shell/project-titles';
import { FakeApi } from '../../testing/fake-api';
import { FakeLive } from '../../testing/fake-live';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { RailState } from './rail-state';
import './shell';

/** Every design §4 path, the rail group it should show, and the breadcrumb it should render. */
const CASES: { path: string; context: string[]; crumbs: string[]; title: string }[] = [
  { path: 'projects', context: [], crumbs: ['Projects'], title: 'Projects' },
  {
    path: 'projects/foundation',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation'],
    title: '',
  },
  {
    path: 'projects/foundation/book',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Book'],
    title: 'Book',
  },
  {
    path: 'projects/foundation/cast',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Cast'],
    title: 'Cast',
  },
  {
    path: 'projects/foundation/cast/abc',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Cast'],
    title: 'Cast',
  },
  {
    path: 'projects/foundation/voices/v1/editor',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Voice editor'],
    title: 'Voice editor',
  },
  {
    path: 'projects/foundation/export',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Export'],
    title: 'Export',
  },
  ...[
    'llm',
    'prompts',
    'tts',
    'voice-design',
    'transcription',
    'similarity',
    'audio',
    'services',
    'themes',
  ].map((page) => ({
    path: `settings/${page}`,
    context: [
      'LLM',
      'Prompts',
      'Paragraph TTS',
      'Voice design',
      'Transcription',
      'Similarity',
      'Audio processing',
      'AI services',
      'Themes',
    ],
    crumbs: ['Settings', ''],
    title: '',
  })),
  { path: 'nowhere', context: [], crumbs: ['Not found'], title: 'Not found' },
];

let live: FakeLive;

/** Lazy route chunks resolve on the macrotask queue, beyond what `settle()` covers. */
const tick = () => new Promise((r) => setTimeout(r, 0));

async function mount(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(routes, async () => true);
  const shell = document.createElement('app-root');
  document.body.append(shell);
  await settle();
  await tick();
  await settle();
  return { shell, router };
}

const texts = (shell: Element, selector: string) =>
  Array.from(shell.querySelectorAll(selector)).map((n) => n.textContent?.trim());

beforeEach(() => {
  resetServices();
  localStorage.clear();
  new FakeApi().install();
  live = new FakeLive().install();
});
afterEach(() => document.body.replaceChildren());

describe('app-root', () => {
  it.each(CASES)('resolves $path with the right rail group and breadcrumb', async (c) => {
    const { shell } = await mount(c.path);

    expect(texts(shell, '.shell__nav-group--context .shell__nav-label')).toEqual(c.context);

    const crumbs = texts(shell, '.shell__crumb');
    expect(crumbs.length).toBe(c.crumbs.length);
    c.crumbs.forEach((expected, i) => {
      if (expected) expect(crumbs[i]).toBe(expected);
    });
    if (c.title) expect(crumbs.at(-1)).toBe(c.title);
    expect(shell.querySelector('.shell__crumb--current')?.textContent?.trim()).toBe(
      crumbs.at(-1) ?? '',
    );
  });

  it('links every crumb but the current page, app-relative', async () => {
    const { shell } = await mount('projects/my%20book/book');
    const links = Array.from(shell.querySelectorAll('a.shell__crumb')).map((a) =>
      a.getAttribute('href'),
    );
    expect(links).toEqual(['projects', 'projects/my%20book']);
    expect(shell.querySelector('.shell__crumb--current')?.textContent).toBe('Book');
  });

  it('names the project crumb by its loaded title, keeping the folder link', async () => {
    const { shell } = await mount('projects/dune/cast');
    use(ProjectTitles).set('dune', 'Dune Messiah');
    await settle();
    expect(texts(shell, '.shell__crumb')).toEqual(['Projects', 'Dune Messiah', 'Cast']);
    expect(shell.querySelectorAll('a.shell__crumb')[1]?.getAttribute('href')).toBe('projects/dune');
    expect(document.title).toBe('Read2Me · Projects · Dune Messiah · Cast');
  });

  it('marks the active nav item and offers the global group', async () => {
    const { shell } = await mount('projects/foundation/book');
    const active = shell.querySelectorAll('.shell__nav-item--active');
    expect(texts(shell, '.shell__nav-item--active .shell__nav-label')).toEqual(['Book']);
    expect(active[0]?.getAttribute('aria-current')).toBe('page');
    expect(texts(shell, '.shell__nav-group--global .shell__nav-label')).toEqual([
      'Projects',
      'Settings',
    ]);
    expect(
      Array.from(shell.querySelectorAll('.shell__nav-group--global a'), (a) =>
        a.getAttribute('href'),
      ),
    ).toEqual(['projects', 'settings']);
  });

  it('keeps Settings active below it and Projects only on the shelf', async () => {
    const { shell } = await mount('settings/prompts');
    expect(texts(shell, '.shell__nav-item--active .shell__nav-label')).toEqual([
      'Prompts',
      'Settings',
    ]);
  });

  it('frames the routed page in the main outlet and opens the project on a project route', async () => {
    const { shell } = await mount('projects/foundation/book');
    expect(shell.querySelector('main r2m-outlet')?.firstElementChild?.localName).toBe(
      'r2m-project-shell',
    );
    expect(shell.querySelector('r2m-project-shell r2m-placeholder-page')).not.toBeNull();
    expect(live.joined).toEqual(['foundation']);
  });

  it('remembers the rail state across instances via localStorage', async () => {
    const { shell } = await mount('projects');
    const rail = shell.querySelector('.shell__rail');
    expect(rail?.classList.contains('shell__rail--expanded')).toBe(true);
    expect(shell.querySelector('.shell__nav-item')?.getAttribute('data-tooltip')).toBeNull();

    shell.querySelector<HTMLElement>('.shell__menu')?.click();
    await settle();
    expect(rail?.classList.contains('shell__rail--expanded')).toBe(false);
    expect(shell.querySelector('.shell__nav-item')?.getAttribute('data-tooltip')).toBe('Projects');
    expect(localStorage.getItem('r2m.rail.expanded')).toBe('0');

    // A fresh RailState (new "page load") reads the persisted value back.
    expect(new RailState().expanded()).toBe(false);
  });

  it('shows the live connection state as a dot with a label', async () => {
    const { shell } = await mount('projects');
    const conn = shell.querySelector('.shell__conn');
    expect(conn?.classList.contains('shell__conn--connected')).toBe(true);
    expect(conn?.getAttribute('aria-label')).toBe('Live updates connected');
    live.state.set('reconnecting');
    await settle();
    expect(conn?.classList.contains('shell__conn--reconnecting')).toBe(true);
    expect(conn?.getAttribute('aria-label')).toBe('Lost');
  });

  it('offers the three scheme choices in the theme menu', async () => {
    const { shell } = await mount('projects');
    expect(shell.querySelector('.shell__theme')?.getAttribute('popovertarget')).toBe(
      'r2m-theme-menu',
    );
    expect(
      Array.from(shell.querySelectorAll('#r2m-theme-menu .r2m-menu__item'), (b) =>
        b.getAttribute('data-scheme'),
      ),
    ).toEqual(['light', 'dark', 'system']);
    expect(shell.querySelector('.shell__settings')?.getAttribute('href')).toBe('settings');
  });
});
