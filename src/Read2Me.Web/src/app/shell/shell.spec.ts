import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { routes } from '@app/app.routes';
import { Router } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';

import { signal } from '@app/core/signals';
import type { NextStep } from '@app/pages/project/next-step';
import type { StepRunner } from '@app/pages/project/step-runner';
import { SetupCheckStore } from '@app/setup-check/setup-check-store';
import { ProjectGuide } from '@app/shell/project-guide';
import { ProjectTitles } from '@app/shell/project-titles';
import { ThemeService } from '@app/theme/theme.service';
import { ToastService } from '@app/ui/toast';
import { ActivityStore } from '@app/activity/activity-store';
import { FakeApi } from '../../testing/fake-api';
import { FakeLive } from '../../testing/fake-live';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { RailState } from './rail-state';
import './shell';

/** Every design §4 path, the rail group it should show, and the breadcrumb it should render. */
const CASES: { path: string; context: string[]; crumbs: string[]; title: string }[] = [
  { path: 'projects', context: [], crumbs: ['Projects'], title: 'Projects' },
  {
    path: 'projects/pride-and-prejudice',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice'],
    title: '',
  },
  {
    path: 'projects/pride-and-prejudice/book',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice', 'Book'],
    title: 'Book',
  },
  {
    path: 'projects/pride-and-prejudice/cast',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice', 'Cast'],
    title: 'Cast',
  },
  {
    path: 'projects/pride-and-prejudice/cast/abc',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice', 'Cast'],
    title: 'Cast',
  },
  {
    path: 'projects/pride-and-prejudice/voices/v1/editor',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice', 'Voice editor'],
    title: 'Voice editor',
  },
  {
    path: 'projects/pride-and-prejudice/export',
    context: ['Overview', 'Book', 'Cast', 'Pronunciations', 'Export'],
    crumbs: ['Projects', 'pride-and-prejudice', 'Export'],
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
      'Setup check',
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

/**
 * Waits until every routed level is rendered, not for a fixed number of ticks: nested outlets
 * load their chunks one after another, and a cold chunk under a full parallel run can take more
 * than one macrotask.
 */
async function mount(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(routes, async () => true);
  const shell = document.createElement('app-root');
  document.body.append(shell);
  await settle();
  const tags = router.match()?.chain.flatMap((r) => (r.tag ? [r.tag] : [])) ?? [];
  for (let i = 0; i < 50 && !tags.every((tag) => shell.querySelector(tag)); i++) {
    await tick();
    await settle();
  }
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
    const { shell } = await mount('projects/dracula/cast');
    use(ProjectTitles).set('dracula', "Dracula's Guest");
    await settle();
    expect(texts(shell, '.shell__crumb')).toEqual(['Projects', "Dracula's Guest", 'Cast']);
    expect(shell.querySelectorAll('a.shell__crumb')[1]?.getAttribute('href')).toBe(
      'projects/dracula',
    );
    expect(document.title).toBe("Read2Me · Projects · Dracula's Guest · Cast");
  });

  it('marks the active nav item and offers the global group', async () => {
    const { shell } = await mount('projects/pride-and-prejudice/book');
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
    const { shell } = await mount('projects/pride-and-prejudice/export');
    expect(shell.querySelector('main r2m-outlet')?.firstElementChild?.localName).toBe(
      'r2m-project-shell',
    );
    expect(shell.querySelector('r2m-project-shell r2m-export-page')).not.toBeNull();
    expect(live.joined).toEqual(['pride-and-prejudice']);
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

describe('Setup check surfacing', () => {
  const STARTED = '2026-10-29T09:00:00Z';

  /** The host's report as `main.ts` loads it before the router starts. */
  async function loadReport(failed: number, firstRun = false, noWorkspace = false) {
    const rows = Array.from({ length: failed + 1 }, (_, i) => ({
      id: `check-${i}`,
      group: 'workspaceAndTools',
      name: `Check ${i}`,
      tier: 'quick',
      result: i < failed ? 'failed' : 'passed',
      expected: 'e',
      found: 'f',
      note: null,
      fix: { text: 'Fix it.', link: null, copy: null },
      managed: false,
      rerun: null,
    }));
    const report = {
      rows,
      quickCheckedAt: '2026-10-29T09:00:05Z',
      fullCheckedAt: null,
      firstRun,
      startedAt: STARTED,
      noWorkspace,
    };
    // The Setup check page, where the first run lands, runs a Quick check as it opens.
    new FakeApi()
      .on('GET', '/api/setup-check', report)
      .on('POST', '/api/setup-check/quick', report)
      .install();
    await use(SetupCheckStore).load();
  }

  it('shows the banner under the app bar and a red shield with the count when a check failed', async () => {
    await loadReport(2);
    const { shell } = await mount('projects');

    const banner = shell.querySelector('.shell__setup-banner');
    expect(banner?.previousElementSibling?.classList.contains('shell__appbar')).toBe(true);
    expect(banner?.textContent).toContain('Setup check: 2 failed.');
    expect(banner?.querySelector('a')?.getAttribute('href')).toBe('settings/setup-check');

    const shield = shell.querySelector('.shell__setup-shield');
    expect(shield?.classList.contains('shell__setup-shield--error')).toBe(true);
    expect(shield?.getAttribute('href')).toBe('settings/setup-check');
    expect(shield?.querySelector('.shell__setup-shield-count')?.textContent).toBe('2');
  });

  it('shows a green shield and no banner when nothing failed', async () => {
    await loadReport(0);
    const { shell } = await mount('projects');
    expect(shell.querySelector('.shell__setup-banner')).toBeNull();
    const shield = shell.querySelector('.shell__setup-shield');
    expect(shield?.classList.contains('shell__setup-shield--ok')).toBe(true);
    expect(shield?.querySelector('.shell__setup-shield-count')).toBeNull();
  });

  it('spins the shield with the step while a Full check runs, on any page', async () => {
    await loadReport(1);
    const { shell } = await mount('projects');
    const report = use(SetupCheckStore).report();
    live.emit('setupCheck', {
      kind: 'progress',
      report: {
        ...report,
        fullRun: {
          step: 1,
          total: 1,
          lane: 'local',
          label: 'Encoding a 1 s tone to .m4b',
          cancelling: false,
        },
      },
    });
    await settle();

    const shield = shell.querySelector('.shell__setup-shield');
    expect(shield?.classList.contains('shell__setup-shield--busy')).toBe(true);
    expect(shield?.getAttribute('aria-label')).toBe('Full check 1/1');
    expect(shield?.querySelector('.r2m-spinner')).not.toBeNull();
    expect(shield?.querySelector('.shell__setup-shield-count')).toBeNull();

    live.emit('setupCheck', { kind: 'done', report: { ...report, fullRun: null } });
    await settle();
    expect(shield?.classList.contains('shell__setup-shield--error')).toBe(true);
    expect(shell.querySelector('.shell__setup-shield .r2m-spinner')).toBeNull();
  });

  it('keeps the banner dismissed across a reload of the same host start', async () => {
    await loadReport(1);
    const { shell } = await mount('projects');
    shell.querySelector<HTMLButtonElement>('.shell__setup-banner-dismiss')?.click();
    await settle();
    expect(shell.querySelector('.shell__setup-banner')).toBeNull();

    document.body.replaceChildren();
    resetServices();
    new FakeLive().install();
    await loadReport(1);
    const reloaded = await mount('projects');
    expect(reloaded.shell.querySelector('.shell__setup-banner')).toBeNull();
    // The shield still says what failed.
    expect(reloaded.shell.querySelector('.shell__setup-shield-count')?.textContent).toBe('1');
  });

  it('lands / on the Setup check on the first run, and on Projects after', async () => {
    await loadReport(0, true);
    expect((await mount('')).router.path()).toBe('settings/setup-check');

    document.body.replaceChildren();
    resetServices();
    new FakeLive().install();
    await loadReport(0);
    expect((await mount('')).router.path()).toBe('projects');
  });

  it.each(['projects', 'projects/pride-and-prejudice/book', 'settings/llm', 'no/such/page'])(
    'renders the Setup check for %s in no-Workspace mode, with no start banner',
    async (path) => {
      await loadReport(1, false, true);
      const { shell } = await mount(path);
      for (let i = 0; i < 50 && !shell.querySelector('[data-role="summary"]'); i++) await settle();

      const main = shell.querySelector('.shell__main');
      expect(main?.querySelector('r2m-outlet')).toBeNull();
      expect(main?.querySelector('r2m-setup-check-page h1')?.textContent).toBe('Setup check');
      expect(main?.querySelector('.r2m-callout--warn')?.textContent).toContain(
        'without a usable Workspace',
      );
      expect(texts(shell, '.shell__crumb')).toEqual(['Setup check']);
      expect(shell.querySelector('.shell__setup-banner')).toBeNull();
    },
  );
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

  async function mountWith(path: string, next: NextStep, folder = 'pride-and-prejudice') {
    const mounted = await mount(path);
    use(ProjectGuide).publish(folder, next, runner);
    await settle();
    return mounted;
  }

  const pill = (shell: Element) => shell.querySelector('.shell__next');

  it('sits after the spacer, then the History button, then the Setup check shield and the live dot on a project page', async () => {
    const { shell } = await mountWith('projects/pride-and-prejudice/book', ACT);
    // The icon buttons share r2m-icon-button as their first class; name them by their own.
    const named = ['shell__history-toggle', 'shell__setup-shield'];
    const bar = Array.from(shell.querySelector('.shell__appbar')?.children ?? []).map(
      (el) => named.find((c) => el.classList.contains(c)) ?? el.classList[0],
    );
    expect(bar.indexOf('shell__next')).toBe(bar.indexOf('shell__spacer') + 1);
    expect(bar.indexOf('shell__history-toggle')).toBe(bar.indexOf('shell__next') + 1);
    expect(bar.indexOf('shell__setup-shield')).toBe(bar.indexOf('shell__history-toggle') + 1);
    expect(bar.indexOf('shell__conn')).toBe(bar.indexOf('shell__setup-shield') + 1);
  });

  it.each([
    {
      why: 'on the Overview',
      path: 'projects/pride-and-prejudice',
      folder: 'pride-and-prejudice',
    },
    {
      why: 'for another project',
      path: 'projects/pride-and-prejudice/book',
      folder: 'dracula',
    },
    {
      why: 'off the project routes',
      path: 'settings/llm',
      folder: 'pride-and-prejudice',
    },
  ])('is hidden $why', async (c) => {
    const { shell } = await mountWith(c.path, ACT, c.folder);
    expect(pill(shell)).toBeNull();
  });

  it('runs the primary action, offers the pills, and opens the Overview from its icon', async () => {
    const { shell } = await mountWith('projects/pride-and-prejudice/cast', ACT);
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
    const { shell } = await mountWith('projects/pride-and-prejudice/book', {
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
    const { shell } = await mountWith('projects/pride-and-prejudice/book', {
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
    const { shell } = await mountWith('projects/pride-and-prejudice/export', {
      kind: 'allDone',
      label: 'All steps done',
      tint: 'ok',
    });
    expect(pill(shell)?.textContent?.replace(/\s+/g, ' ').trim()).toContain('All steps done');
    expect(shell.querySelector('.shell__next-pill')).toBeNull();
    expect(shell.querySelector('.shell__next-overview')).not.toBeNull();
  });

  it('disables its buttons while a step command runs', async () => {
    const { shell } = await mountWith('projects/pride-and-prejudice/book', ACT);
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
      .on('GET', '/api/projects/pride-and-prejudice/history', {
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
            kind: 'started',
            action: null,
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
    expect(toggle((await mount('projects/pride-and-prejudice/cast')).shell)).not.toBeNull();
    document.body.replaceChildren();
    expect(toggle((await mount('projects')).shell)).toBeNull();
  });

  it('opens beside the page with the advice line and the steps, and closes again', async () => {
    const { shell } = await mount('projects/pride-and-prejudice/book');
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
    const { shell, router } = await mount('projects/pride-and-prejudice/book');
    toggle(shell)?.click();
    await settle();

    router.navigate('projects/pride-and-prejudice/cast');
    await settle();
    await tick();
    await settle();

    expect(shell.querySelector('.shell__history r2m-history-sheet')).not.toBeNull();
  });
});

describe('viewing a History step', () => {
  const at = new Date().toISOString();
  const row = (sequence: number, label: string, summary: string | null) => ({
    stepId: `s${sequence}`,
    parentStepId: sequence > 1 ? `s${sequence - 1}` : null,
    sequence,
    label,
    summary,
    author: 'dean',
    startedAt: at,
    endedAt: at,
    isOpen: false,
    note: null,
    kind: 'request',
    action: 'edit',
    restoredFromStepId: null,
    cantOpen: false,
  });
  let api: FakeApi;
  let toasts: string[];

  beforeEach(() => {
    toasts = [];
    override(ToastService, { info: (m: string) => toasts.push(m) } as unknown as ToastService);
    api = new FakeApi().on('GET', '/api/projects/pride-and-prejudice/history', {
      steps: [row(2, 'Assign speaker', null), row(1, 'Split chapter', 'Chapter 3 at paragraph 41')],
      headStepId: 's2',
      busy: null,
      restoring: false,
    });
    api.install();
  });

  async function openSheet(path = 'projects/pride-and-prejudice/book') {
    const mounted = await mount(path);
    use(ProjectGuide).publish(
      'pride-and-prejudice',
      { kind: 'allDone', label: 'All done', tint: 'done' } as unknown as NextStep,
      { busy: signal(null) } as unknown as StepRunner,
    );
    mounted.shell.querySelector<HTMLButtonElement>('.shell__history-toggle')?.click();
    await settle();
    await tick();
    await settle();
    return mounted;
  }

  const stepButton = (shell: Element, id: string) =>
    shell.querySelector<HTMLButtonElement>(`[data-step-id="${id}"] .history-sheet__step`);
  const main = (shell: Element) => shell.querySelector<HTMLElement>('.shell__main');

  it('shows the banner above the body over a greyed page it leaves browsable, and hides the next-step pill', async () => {
    const { shell } = await openSheet();
    expect(shell.querySelector('.shell__next')).not.toBeNull();

    stepButton(shell, 's1')?.click();
    await settle();

    const banner = shell.querySelector('.shell__viewing');
    expect(banner?.nextElementSibling?.classList.contains('shell__body')).toBe(true);
    expect(banner?.textContent).toContain('#1 Split chapter');
    expect(banner?.textContent).toContain('Chapter 3 at paragraph 41');
    expect(banner?.textContent).toContain('Today');
    // Not inert (ticket 32): tabs, the tree, rows and play stay usable; edit controls are off.
    expect(main(shell)?.inert).toBe(false);
    expect(main(shell)?.classList.contains('shell__main--viewing')).toBe(true);
    expect(shell.querySelector('.shell__next')).toBeNull();
    expect(stepButton(shell, 's1')?.getAttribute('aria-pressed')).toBe('true');
  });

  it('returns to the current project from the banner, or by clicking the tip', async () => {
    const { shell } = await openSheet();

    stepButton(shell, 's1')?.click();
    await settle();
    shell.querySelector<HTMLButtonElement>('.shell__viewing-back')?.click();
    await settle();
    expect(shell.querySelector('.shell__viewing')).toBeNull();
    expect(main(shell)?.inert).toBe(false);

    stepButton(shell, 's1')?.click();
    await settle();
    stepButton(shell, 's2')?.click();
    await settle();
    expect(shell.querySelector('.shell__viewing')).toBeNull();
  });

  it("can't view while busy: the sheet says why and opens the activity drawer", async () => {
    const { shell } = await openSheet();
    live.emit('history', {
      kind: 'busyChanged',
      folder: 'pride-and-prejudice',
      reason: 'Attribution: 128 paragraphs left',
    });
    await settle();

    expect(stepButton(shell, 's1')?.disabled).toBe(true);
    expect(shell.querySelector('.history-sheet__busy')?.textContent).toContain(
      'Attribution: 128 paragraphs left',
    );
    shell.querySelector<HTMLButtonElement>('.history-sheet__busy button')?.click();
    await settle();
    expect(use(ActivityStore).drawerOpen()).toBe(true);
  });

  it('goes back to current with a toast when background work starts', async () => {
    const { shell } = await openSheet();
    stepButton(shell, 's1')?.click();
    await settle();

    live.emit('history', {
      kind: 'busyChanged',
      folder: 'pride-and-prejudice',
      reason: 'Voice batch',
    });
    await settle();

    expect(shell.querySelector('.shell__viewing')).toBeNull();
    expect(toasts).toHaveLength(1);
  });

  it('states what a Restore costs before the click, then restores with no confirm', async () => {
    api.on('GET', '/api/projects/pride-and-prejudice/history/s1', {
      ...row(1, 'Split chapter', null),
      restorePreview: { audioDiscardCount: 60 },
    });
    api.on('POST', '/api/projects/pride-and-prejudice/history/s1/restore', {
      step: { ...row(3, "Restore to 'Split chapter' (14:02)", null), restoredFromStepId: 's1' },
      audioDiscardCount: 60,
    });
    const { shell } = await openSheet();
    stepButton(shell, 's1')?.click();
    await settle();
    const restore = () => shell.querySelector<HTMLButtonElement>('.shell__viewing-restore');
    await tick();
    await settle();

    expect(shell.querySelector('.shell__viewing')?.textContent).toContain(
      'Restoring discards audio on 60 items',
    );
    expect(restore()?.disabled).toBe(false);

    restore()?.click();
    await tick();
    await settle();

    expect(api.calls('POST', '/api/projects/pride-and-prejudice/history/s1/restore')).toHaveLength(
      1,
    );
    expect(shell.querySelector('.shell__viewing')).toBeNull();
    expect(main(shell)?.inert).toBe(false);
    expect(toasts.at(-1)).toContain('Restored to #1 Split chapter');
  });
});
