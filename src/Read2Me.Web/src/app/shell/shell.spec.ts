import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { routes } from '@app/app.routes';
import { Router } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';

import { signal } from '@app/core/signals';
import type { NextStep } from '@app/pages/project/next-step';
import type { StepRunner } from '@app/pages/project/step-runner';
import { ProjectGuide } from '@app/shell/project-guide';
import { ProjectTitles } from '@app/shell/project-titles';
import { ThemeService } from '@app/theme/theme.service';
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
    // Every project child is ported now; the export page stands in for them here.
    const { shell } = await mount('projects/foundation/export');
    expect(shell.querySelector('main r2m-outlet')?.firstElementChild?.localName).toBe(
      'r2m-project-shell',
    );
    expect(shell.querySelector('r2m-project-shell r2m-export-page')).not.toBeNull();
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

  it('exposes the live connection state on its host for tests to wait on', async () => {
    const { shell } = await mount('projects');
    expect(shell.getAttribute('data-live-state')).toBe('connected');
    live.state.set('reconnecting');
    await settle();
    expect(shell.getAttribute('data-live-state')).toBe('reconnecting');
  });

  it('starts with the activity drawer closed and mounts it only while open', async () => {
    const { shell } = await mount('projects');
    expect(shell.drawerOpen()).toBe(false);
    expect(shell.querySelector('r2m-activity-bar')).not.toBeNull();
    expect(shell.querySelector('r2m-activity-drawer')).toBeNull();

    shell.querySelector<HTMLElement>('[aria-label="Toggle activity drawer"]')?.click();
    await settle();
    expect(shell.drawerOpen()).toBe(true);
    expect(shell.querySelector('.shell__drawer r2m-activity-drawer')).not.toBeNull();

    shell.querySelector<HTMLElement>('[aria-label="Close activity drawer"]')?.click();
    await settle();
    expect(shell.querySelector('r2m-activity-drawer')).toBeNull();
  });

  it.each([
    { page: 'llm', tag: 'r2m-llm-settings-page', title: 'LLM' },
    { page: 'prompts', tag: 'r2m-prompts-page', title: 'Prompts' },
    { page: 'tts', tag: 'r2m-tts-settings-page', title: 'Paragraph TTS' },
    { page: 'voice-design', tag: 'r2m-voice-design-settings-page', title: 'Voice design' },
    { page: 'transcription', tag: 'r2m-transcription-settings-page', title: 'Transcription' },
    { page: 'similarity', tag: 'r2m-similarity-settings-page', title: 'Similarity' },
    { page: 'audio', tag: 'r2m-audio-settings-page', title: 'Audio processing' },
    { page: 'services', tag: 'r2m-ai-services-page', title: 'AI services' },
    { page: 'themes', tag: 'r2m-themes-page', title: 'Themes' },
  ])('frames settings/$page as $tag with its crumbs and tab title', async (c) => {
    const { shell } = await mount(`settings/${c.page}`);

    expect(shell.querySelector('main > r2m-outlet')?.firstElementChild?.localName).toBe(c.tag);
    expect(shell.querySelector('r2m-not-found-page')).toBeNull();
    expect(texts(shell, '.shell__crumb')).toEqual(['Settings', c.title]);
    expect(document.title).toBe(`Read2Me · Settings · ${c.title}`);
  });

  it('switches the scheme from the theme menu through ThemeService', async () => {
    const { shell } = await mount('projects');
    const chosen: string[] = [];
    use(ThemeService).setPreference = async (preference) => {
      chosen.push(preference);
    };
    // happy-dom has no popover API: record the hide the menu asks for.
    let hidden = 0;
    const menu = shell.querySelector<HTMLElement>('#r2m-theme-menu');
    (menu as unknown as { hidePopover: () => void }).hidePopover = () => void hidden++;

    menu?.querySelector<HTMLElement>('[data-scheme="dark"]')?.click();

    expect(chosen).toEqual(['dark']);
    expect(hidden).toBe(1);
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

describe('next-step pill', () => {
  let calls: string[];
  let runner: StepRunner;

  beforeEach(() => {
    calls = [];
    runner = {
      busy: signal<string | null>(null),
      act: async (e: { step: string; action: string }) =>
        void calls.push(`act ${e.step}:${e.action}`),
      checkpoint: async (e: { step: string; pill: string }) =>
        void calls.push(`checkpoint ${e.step}:${e.pill}`),
      openOverview: () => void calls.push('overview'),
    } as unknown as StepRunner;
  });

  const ACT: NextStep = {
    kind: 'act',
    step: 'titles',
    label: 'Next: Titles · Add titles…',
    tint: 'next',
    action: 'addTitles',
    tooltip: 'Usually done after Structure: a reread drops inserted titles.',
    pills: [
      { id: 'confirm', label: 'Confirm', state: 'confirmed' },
      { id: 'skip', label: 'Skip', state: 'skipped' },
    ],
  };

  async function mountWith(path: string, next: NextStep, folder = 'foundation') {
    const mounted = await mount(path);
    use(ProjectGuide).publish(folder, next, runner);
    await settle();
    return mounted;
  }

  const pill = (shell: Element) => shell.querySelector('.shell__next');

  it('sits after the spacer, then the History button, then the live dot on a project page', async () => {
    const { shell } = await mountWith('projects/foundation/book', ACT);
    const bar = Array.from(shell.querySelector('.shell__appbar')?.children ?? []).map((el) =>
      el.classList.contains('shell__history-toggle') ? 'shell__history-toggle' : el.classList[0],
    );
    expect(bar.indexOf('shell__next')).toBe(bar.indexOf('shell__spacer') + 1);
    expect(bar.indexOf('shell__history-toggle')).toBe(bar.indexOf('shell__next') + 1);
    expect(bar.indexOf('shell__conn')).toBe(bar.indexOf('shell__history-toggle') + 1);
  });

  it.each([
    {
      why: 'on the Overview',
      path: 'projects/foundation',
      folder: 'foundation',
    },
    {
      why: 'for another project',
      path: 'projects/foundation/book',
      folder: 'dune',
    },
    {
      why: 'off the project routes',
      path: 'settings/llm',
      folder: 'foundation',
    },
  ])('is hidden $why', async (c) => {
    const { shell } = await mountWith(c.path, ACT, c.folder);
    expect(pill(shell)).toBeNull();
  });

  it('runs the primary action, offers the pills, and opens the Overview from its icon', async () => {
    const { shell } = await mountWith('projects/foundation/cast', ACT);
    const main = shell.querySelector<HTMLElement>('.shell__next-main');
    expect(main?.textContent?.trim()).toBe('Next: Titles · Add titles…');
    expect(main?.getAttribute('data-tooltip')).toBe(ACT.kind === 'act' ? ACT.tooltip : null);
    main?.click();
    shell.querySelectorAll<HTMLElement>('.shell__next-pill').forEach((b) => b.click());
    shell.querySelector<HTMLElement>('.shell__next-overview')?.click();
    expect(calls).toEqual([
      'act titles:addTitles',
      'checkpoint titles:confirm',
      'checkpoint titles:skip',
      'overview',
    ]);
    expect(shell.querySelector('.shell__next-overview')?.getAttribute('aria-label')).toBe(
      'Open Overview',
    );
  });

  it('a stale step has the warn tint, still runs its action and offers Reconfirm', async () => {
    const { shell } = await mountWith('projects/foundation/book', {
      kind: 'stale',
      step: 'structure',
      label: 'Structure changed since you confirmed',
      tint: 'warn',
      action: 'checkStructure',
      tooltip: null,
      pills: [{ id: 'reconfirm', label: 'Reconfirm', state: 'confirmed' }],
    });
    expect(pill(shell)?.classList.contains('shell__next--warn')).toBe(true);
    shell.querySelector<HTMLElement>('.shell__next-main')?.click();
    expect(texts(shell, '.shell__next-pill')).toEqual(['Reconfirm']);
    expect(calls).toEqual(['act structure:checkStructure']);
  });

  it('a running or blocked step shows step · chip and its pills; the click opens the Overview with the reason as tooltip', async () => {
    const { shell } = await mountWith('projects/foundation/book', {
      kind: 'waiting',
      step: 'speakers',
      label: 'Speakers · Review not confirmed',
      tint: 'neutral',
      tooltip: 'Every paragraph has a speaker',
      pills: [{ id: 'confirm', label: 'Confirm review', state: 'confirmed' }],
    });
    const main = shell.querySelector<HTMLElement>('.shell__next-main');
    expect(pill(shell)?.classList.contains('shell__next--neutral')).toBe(true);
    expect(main?.getAttribute('data-tooltip')).toBe('Every paragraph has a speaker');
    main?.click();
    shell.querySelector<HTMLElement>('.shell__next-pill')?.click();
    expect(calls).toEqual(['overview', 'checkpoint speakers:confirm']);
  });

  it('reads “All steps done” beside the Overview icon', async () => {
    const { shell } = await mountWith('projects/foundation/export', {
      kind: 'allDone',
      label: 'All steps done',
      tint: 'ok',
    });
    expect(pill(shell)?.textContent?.replace(/\s+/g, ' ').trim()).toContain('All steps done');
    expect(shell.querySelector('.shell__next-pill')).toBeNull();
    expect(shell.querySelector('.shell__next-overview')).not.toBeNull();
  });

  it('disables its buttons while a step command runs', async () => {
    const { shell } = await mountWith('projects/foundation/book', ACT);
    (runner.busy as ReturnType<typeof signal<string | null>>).set('titles:confirm');
    await settle();
    expect(shell.querySelector<HTMLButtonElement>('.shell__next-main')?.disabled).toBe(true);
    expect(shell.querySelector<HTMLButtonElement>('.shell__next-pill')?.disabled).toBe(true);
  });
});

describe('History sheet', () => {
  const toggle = (shell: Element) =>
    shell.querySelector<HTMLButtonElement>('.shell__history-toggle');

  beforeEach(() => {
    new FakeApi()
      .on('GET', '/api/projects/foundation/history', {
        steps: [
          {
            stepId: 's1',
            parentStepId: null,
            sequence: 1,
            label: 'History started',
            summary: null,
            author: 'dean',
            startedAt: new Date().toISOString(),
            endedAt: new Date().toISOString(),
            isOpen: false,
            note: null,
            restoredFromStepId: null,
            cantOpen: false,
          },
        ],
        headStepId: 's1',
        busy: null,
        restoring: false,
      })
      .install();
  });

  it('has an app-bar button on a project page only', async () => {
    expect(toggle((await mount('projects/foundation/cast')).shell)).not.toBeNull();
    document.body.replaceChildren();
    expect(toggle((await mount('projects')).shell)).toBeNull();
  });

  it('opens beside the page with the advice line and the steps, and closes again', async () => {
    const { shell } = await mount('projects/foundation/book');
    toggle(shell)?.click();
    await settle();
    await tick();
    await settle();

    const sheet = shell.querySelector('.shell__body > .shell__history r2m-history-sheet');
    expect(sheet).not.toBeNull();
    expect(toggle(shell)?.getAttribute('aria-pressed')).toBe('true');
    expect(sheet?.querySelector('.r2m-advice')?.textContent).toContain('nothing is lost');
    expect(texts(shell, '.history-sheet__label')).toEqual(['History started']);
    expect(texts(shell, '.history-sheet__current')).toEqual(['Current']);

    shell.querySelector<HTMLButtonElement>('[aria-label="Close History"]')?.click();
    await settle();
    expect(shell.querySelector('.shell__history')).toBeNull();
  });

  it('stays open from one page of the project to the next', async () => {
    const { shell, router } = await mount('projects/foundation/book');
    toggle(shell)?.click();
    await settle();

    router.navigate('projects/foundation/cast');
    await settle();
    await tick();
    await settle();

    expect(shell.querySelector('.shell__history r2m-history-sheet')).not.toBeNull();
  });
});
