import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { installNavigation, settle } from '../../../testing/fake-navigation';
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

  it('leaves the group when the shell leaves the page', async () => {
    const { shell } = await start('projects/dune/book');
    shell.remove();
    expect(live.left).toEqual(['dune']);
  });
});
