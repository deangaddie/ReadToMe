import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import './placeholder-page';

async function start(path: string) {
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start([{ path: '*', tag: 'r2m-placeholder-page' }], async () => true);
  const page = document.createElement('r2m-placeholder-page');
  document.body.append(page);
  await page.rendered();
  return { page, router };
}

const link = (page: Element) =>
  page.querySelector('.r2m-placeholder-page__link')?.getAttribute('href');

beforeEach(() => resetServices());
afterEach(() => document.body.replaceChildren());

describe('r2m-placeholder-page', () => {
  it('says the screen has not moved yet', async () => {
    const { page } = await start('projects/dune/book');
    expect(page.querySelector('.r2m-empty-state__headline')?.textContent).toContain(
      'has not moved to the native app yet',
    );
  });

  it('links to the same path in the Angular app', async () => {
    const { page } = await start('projects/my%20book/cast');
    expect(link(page)).toBe('/app/projects/my%20book/cast');
  });

  it('links to the Angular root from the native root', async () => {
    const { page } = await start('');
    expect(link(page)).toBe('/app/');
  });

  it('follows the path as it changes', async () => {
    const { page, router } = await start('projects');
    router.navigate('settings/llm');
    await settle();
    expect(link(page)).toBe('/app/settings/llm');
  });
});
