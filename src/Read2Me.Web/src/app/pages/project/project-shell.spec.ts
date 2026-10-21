import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import type { ProjectStatusDto } from '@app/api';
import { ProjectGuide } from '@app/shell/project-guide';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { installNavigation, settle } from '../../../testing/fake-navigation';
import { HistoryStore } from '@app/history/history-store';
import { ProjectStore } from './project-store';
import './project-shell';

class ChildPage extends R2mElement {
  protected template() {
    return html`<p>${use(ProjectStore, this).folder()}</p>`;
  }
}
define('x-project-child', ChildPage);

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: 'book', tag: 'x-project-child' }],
  },
  { path: 'elsewhere', tag: 'x-project-child' },
];

/** A project before its book is read: Import is the next step. */
const EMPTY_STATUS: ProjectStatusDto = {
  hasContent: false,
  characters: 0,
  charactersWithLines: 0,
  readyVoices: 0,
  items: { total: 0, withAudio: 0, unattributed: 0 },
  attribution: { remaining: 0, processing: false, queued: 0 },
  audio: { remaining: 0 },
  review: 0,
  volumeIds: [],
  nodes: {},
  pauseParagraphs: 0,
  checkpoints: {},
  revision: 1,
};

let live: FakeLive;
let api: FakeApi;

/** Mounted through a root outlet, as in the app: the shell's own outlet is then level 1. */
async function start(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  await settle();
  const shell = outlet.querySelector('r2m-project-shell');
  if (!shell) throw new Error('the project shell did not render');
  return { shell, router };
}

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
});
afterEach(() => document.body.replaceChildren());

describe('r2m-project-shell', () => {
  it('opens the route’s project: joins its hub group and loads detail + status', async () => {
    await start('projects/dune/book');
    expect(live.joined).toEqual(['dune']);
    expect(api.calls('GET', '/api/projects/dune')).toHaveLength(1);
    expect(api.calls('GET', '/api/projects/dune/status')).toHaveLength(1);
  });

  it('provides the store to the child route it renders', async () => {
    const { shell } = await start('projects/dune/book');
    expect(shell.querySelector('x-project-child')?.textContent).toBe('dune');
  });

  it('switches project when the folder param changes without re-mounting', async () => {
    const { shell, router } = await start('projects/dune/book');
    const child = shell.querySelector('x-project-child');
    router.navigate('projects/emma/book');
    await settle();
    expect(live.left).toEqual(['dune']);
    expect(live.joined).toEqual(['dune', 'emma']);
    expect(shell.querySelector('x-project-child')).toBe(child);
  });

  it('a query-only navigation keeps the project open', async () => {
    const { router } = await start('projects/dune/book');
    router.navigate('projects/dune/book', { replace: true, query: { chapter: 'c2' } });
    await settle();
    expect(live.left).toEqual([]);
    expect(live.joined).toEqual(['dune']);
    expect(api.calls('GET', '/api/projects/dune')).toHaveLength(1);
  });

  it('publishes the project’s next step into the guide, and clears it on leaving', async () => {
    api.on('GET', '/api/projects/dune/status', EMPTY_STATUS);
    const { shell } = await start('projects/dune/book');
    await settle();
    const guide = use(ProjectGuide);
    expect(guide.current()?.folder).toBe('dune');
    expect(guide.current()?.next).toMatchObject({
      kind: 'act',
      label: 'Next: Import · Read book',
    });
    shell.remove();
    expect(guide.current()).toBeNull();
  });

  it('leaves the group when the shell leaves the page', async () => {
    const { shell } = await start('projects/dune/book');
    shell.remove();
    expect(live.left).toEqual(['dune']);
  });

  it('rereads the project at a viewed step and mounts the page afresh, and again back at current', async () => {
    api.on('GET', '/api/projects/dune/status', EMPTY_STATUS);
    const { shell } = await start('projects/dune/book');
    const history = use(HistoryStore, shell.querySelector('x-project-child')!);
    const page = shell.querySelector('x-project-child');

    history.view('s1');
    await settle();

    expect(api.calls('GET', '/api/projects/dune').map((r) => r.search)).toEqual(['', '?at=s1']);
    expect(api.calls('GET', '/api/projects/dune/status').at(-1)?.search).toBe('?at=s1');
    const viewed = shell.querySelector('x-project-child');
    expect(viewed).not.toBeNull();
    expect(viewed).not.toBe(page);

    history.backToCurrent();
    await settle();

    expect(api.calls('GET', '/api/projects/dune').at(-1)?.search).toBe('');
    expect(shell.querySelector('x-project-child')).not.toBe(viewed);
  });

  it('keeps the past detail when a current read lands after it', async () => {
    let answerCurrent!: (r: unknown) => void;
    api.on('GET', '/api/projects/dune', (_body, url) =>
      url.search
        ? { folderName: 'dune', title: 'Past' }
        : new Promise((resolve) => (answerCurrent = resolve)),
    );
    const { shell } = await start('projects/dune/book');
    const child = shell.querySelector('x-project-child')!;

    use(HistoryStore, child).view('s1');
    await settle();
    answerCurrent({ folderName: 'dune', title: 'Current' });
    await settle();

    expect(use(ProjectStore, shell.querySelector('x-project-child')!).detail()?.title).toBe('Past');
  });
});
