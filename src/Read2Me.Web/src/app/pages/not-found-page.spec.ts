import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { installNavigation } from '../../testing/fake-navigation';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import './not-found-page';

const ROUTES: RouteDef[] = [
  { path: 'projects', title: 'Projects', tag: 'x-projects' },
  { path: '*', title: 'Not found', tag: 'r2m-not-found-page' },
];

async function start(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const page = document.createElement('r2m-not-found-page');
  document.body.append(page);
  await page.rendered();
  return page;
}

beforeEach(() => resetServices());
afterEach(() => document.body.replaceChildren());

describe('r2m-not-found-page', () => {
  it('names the route title as its heading', async () => {
    const page = await start('no/such/screen');
    expect(page.querySelector('.r2m-page-header__title')?.textContent).toBe('Not found');
  });

  it('says the page does not exist and links back to the projects shelf', async () => {
    const page = await start('no/such/screen');
    expect(page.querySelector('.r2m-empty-state__headline')?.textContent).toContain(
      'This page does not exist',
    );
    expect(page.querySelector('.r2m-not-found-page__link')?.getAttribute('href')).toBe('projects');
  });
});
