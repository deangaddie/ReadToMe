import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import './placeholder-page';

const ROUTES: RouteDef[] = [
  {
    path: 'projects',
    title: 'Projects',
    children: [
      { path: '', tag: 'r2m-placeholder-page' },
      {
        path: ':folder',
        title: (p) => p['folder'] ?? '',
        tag: 'r2m-placeholder-page',
        children: [
          { path: '', title: 'Overview', tag: 'r2m-placeholder-page' },
          { path: 'cast', title: 'Cast', tag: 'r2m-placeholder-page' },
        ],
      },
    ],
  },
  {
    path: 'settings',
    title: 'Settings',
    children: [{ path: 'llm', title: 'LLM', tag: 'r2m-placeholder-page' }],
  },
  { path: '*', title: 'Not found', tag: 'r2m-placeholder-page' },
];

async function start(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const page = document.createElement('r2m-placeholder-page');
  document.body.append(page);
  await page.rendered();
  return { page, router };
}

const link = (page: Element) =>
  page.querySelector('.r2m-placeholder-page__link')?.getAttribute('href');
const heading = (page: Element) => page.querySelector('.r2m-page-header__title')?.textContent;

beforeEach(() => resetServices());
afterEach(() => document.body.replaceChildren());

describe('r2m-placeholder-page', () => {
  it('says the screen has not moved yet', async () => {
    const { page } = await start('projects/dune/cast');
    expect(page.querySelector('.r2m-empty-state__headline')?.textContent).toContain(
      'has not moved to the native app yet',
    );
  });

  it('names the screen from the route chain, with the project as subtitle', async () => {
    const { page } = await start('projects/dune/cast');
    expect(heading(page)).toBe('Cast');
    expect(page.querySelector('.r2m-page-header__subtitle')?.textContent).toBe('Project: dune');
  });

  it('names the overview and the shelf', async () => {
    const { page, router } = await start('projects/dune');
    expect(heading(page)).toBe('Overview');
    router.navigate('projects');
    await settle();
    expect(heading(page)).toBe('Projects');
    expect(page.querySelector('.r2m-page-header__subtitle')).toBeNull();
  });

  it('links to the same path in the Angular app', async () => {
    const { page } = await start('projects/my%20book/cast');
    expect(link(page)).toBe('/app/projects/my%20book/cast');
  });

  it('links to the Angular root from the native root', async () => {
    const { page } = await start('');
    expect(link(page)).toBe('/app/');
    expect(heading(page)).toBe('Not found');
  });

  it('follows the path as it changes', async () => {
    const { page, router } = await start('projects');
    router.navigate('settings/llm');
    await settle();
    expect(link(page)).toBe('/app/settings/llm');
    expect(heading(page)).toBe('LLM');
  });
});
