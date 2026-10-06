import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { html } from 'lit-html';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { R2mElement, define } from '@app/core/element';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import './shell';

class Page extends R2mElement {
  protected template() {
    return html`<p>page</p>`;
  }
}
define('x-shell-page', Page);

class ProjectPage extends R2mElement {
  protected template() {
    return html`<r2m-outlet></r2m-outlet>`;
  }
}
define('x-shell-project', ProjectPage);

const ROUTES: RouteDef[] = [
  { path: 'projects', title: 'Projects', tag: 'x-shell-page' },
  {
    path: 'projects/:folder',
    title: (p) => p['folder'] ?? '',
    tag: 'x-shell-project',
    children: [{ path: 'book', title: 'Book', tag: 'x-shell-page' }],
  },
  { path: 'untitled', tag: 'x-shell-page' },
];

async function start(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const shell = document.createElement('app-root');
  document.body.append(shell);
  await settle();
  return { shell, router };
}

const crumbs = (shell: Element) =>
  Array.from(shell.querySelectorAll('.shell__crumbs > a, .shell__crumbs > [aria-current]')).map(
    (c) => ({ text: c.textContent, href: c.getAttribute('href') }),
  );

beforeEach(() => resetServices());
afterEach(() => document.body.replaceChildren());

describe('app-root', () => {
  it('frames the routed page with an app bar', async () => {
    const { shell } = await start('projects');
    expect(shell.querySelector('.shell__appbar .shell__brand')?.textContent).toContain('Read2Me');
    expect(shell.querySelector('.shell__brand')?.getAttribute('href')).toBe('./');
    expect(shell.querySelector('main r2m-outlet')?.firstElementChild?.localName).toBe(
      'x-shell-page',
    );
  });

  it('builds breadcrumbs from the route chain, linking all but the current page', async () => {
    const { shell } = await start('projects/my%20book/book');
    expect(crumbs(shell)).toEqual([
      { text: 'my book', href: 'projects/my%20book' },
      { text: 'Book', href: null },
    ]);
    expect(shell.querySelector('.shell__crumbs [aria-current="page"]')?.textContent).toBe('Book');
  });

  it('follows the route as it changes', async () => {
    const { shell, router } = await start('projects/dune/book');
    router.navigate('projects');
    await settle();
    expect(crumbs(shell)).toEqual([{ text: 'Projects', href: null }]);
  });

  it('shows no breadcrumbs for a route without a title or an unknown path', async () => {
    const { shell, router } = await start('untitled');
    expect(crumbs(shell)).toEqual([]);
    router.navigate('nowhere');
    await settle();
    expect(crumbs(shell)).toEqual([]);
  });
});
