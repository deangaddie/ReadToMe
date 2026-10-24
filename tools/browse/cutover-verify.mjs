// Cutover smoke against the host's /app bundle: shell on a throwaway book project, the reader, an unknown path, favicon.
import { launch, openWeb, WEB, bookProject } from './browse.mjs';

const { folder, remove } = await bookProject('alices-adventures-in-wonderland');
const { page, close } = await launch();
try {
  await openWeb(page, `/projects/${folder}/book`);
  await page.waitForSelector('.tree__title');
  console.log('url:', page.url(), 'title:', await page.title());
  console.log('crumbs:', await page.$$eval('.shell__crumb', (e) => e.map((x) => x.textContent.trim())));
  console.log('tree titles:', (await page.$$eval('.tree__title', (e) => e.map((x) => x.textContent.trim()))).slice(0, 4));
  console.log('live:', await page.locator('.shell__conn').getAttribute('aria-label'));
  await openWeb(page, '/no/such/screen');
  await page.waitForSelector('r2m-not-found-page .r2m-page-header__title');
  console.log('not found:', await page.locator('r2m-not-found-page .r2m-empty-state__headline').textContent());
  await page.click('a.r2m-not-found-page__link');
  await page.waitForFunction(() => location.pathname === '/app/projects');
  console.log('after link:', page.url(), await page.locator('r2m-projects-page').count());
  const icon = await page.evaluate(async () => {
    const href = document.querySelector('link[rel=icon]').href;
    const r = await fetch(href);
    return `${r.status} ${r.headers.get('content-type')} ${r.headers.get('cache-control')}`;
  });
  console.log('favicon:', icon, 'from', WEB);
  await page.screenshot({ path: 'cutover-verify.png' });
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await remove();
  await close();
}
