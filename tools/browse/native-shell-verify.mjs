// Verifies native-web 20 against the host's /app2 bundle: theme on boot, shell nav + crumbs,
// live dot, theme quick menu, placeholder. Restores the theme selection it found.
//   MSYS_NO_PATHCONV=1 R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun native-shell-verify.mjs
import { launch, openWeb, api, HOST } from './browse.mjs';

const texts = (page, sel) => page.locator(sel).allTextContents().then((t) => t.map((s) => s.trim()));
const put = (body) =>
  fetch(`${HOST}/api/settings/themes/selection`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then((r) => r.json());

const initial = await api('/api/settings/themes/selection');
const themes = await api('/api/settings/themes');
const byName = (n) => themes.find((t) => t.name === n);
console.log('initial selection:', initial, 'themes:', themes.map((t) => `${t.id}:${t.name}${t.isDark ? '(dark)' : ''}`).join(' '));

const { page, close } = await launch();
try {
  await put({ selectedThemeId: byName('Light').id, followSystemPreference: false });
  await openWeb(page, '/projects/foundation/book');
  await page.waitForSelector('app-root .shell__brand');
  console.log('url:', page.url());
  console.log('data-theme on boot:', await page.locator('html').getAttribute('data-theme'));
  console.log('style#r2m-theme head:', (await page.locator('#r2m-theme').textContent()).split('\n').slice(0, 3).join(' '));
  console.log('tab title:', await page.title());
  console.log('crumbs:', await texts(page, '.shell__crumb'));
  console.log('context rail:', await texts(page, '.shell__nav-group--context .shell__nav-label'));
  console.log('global rail:', await texts(page, '.shell__nav-group--global .shell__nav-label'));
  console.log('active:', await texts(page, '.shell__nav-item--active .shell__nav-label'));
  await page.waitForSelector('.shell__conn--connected', { timeout: 10000 });
  console.log('live dot:', await page.locator('.shell__conn').getAttribute('aria-label'));
  console.log('placeholder title:', await texts(page, 'r2m-placeholder-page .r2m-page-header__title'),
    'link:', await page.locator('a.r2m-placeholder-page__link').getAttribute('href'));

  // Theme quick menu → Dark, then back to Light.
  await page.locator('.shell__theme').click();
  await page.waitForSelector('#r2m-theme-menu:popover-open');
  await page.locator('#r2m-theme-menu [data-scheme="dark"]').click();
  await page.waitForFunction(() => document.documentElement.dataset.theme === 'dark');
  console.log('after Dark:', await page.locator('html').getAttribute('data-theme'), await api('/api/settings/themes/selection'));
  console.log('body bg (dark):', await page.evaluate(() => getComputedStyle(document.body).backgroundColor));
  await page.locator('.shell__theme').click();
  await page.locator('#r2m-theme-menu [data-scheme="light"]').click();
  await page.waitForFunction(() => document.documentElement.dataset.theme === 'light');
  console.log('after Light:', await page.locator('html').getAttribute('data-theme'), await api('/api/settings/themes/selection'));
  console.log('body bg (light):', await page.evaluate(() => getComputedStyle(document.body).backgroundColor));

  // Rail navigation to Cast, then settings via the app bar.
  await page.locator('.shell__nav-group--context a', { hasText: 'Cast' }).click();
  await page.waitForFunction(() => location.pathname.endsWith('/foundation/cast'));
  console.log('after Cast click:', await texts(page, '.shell__crumb'), await texts(page, 'r2m-placeholder-page .r2m-page-header__title'));
  await page.locator('.shell__settings').click();
  await page.waitForFunction(() => location.pathname.endsWith('/settings/llm'));
  console.log('settings:', await texts(page, '.shell__crumb'), (await texts(page, '.shell__nav-group--context .shell__nav-label')).length, 'items');
  await page.screenshot({ path: 'native-shell-verify.png', fullPage: false });
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await put(initial);
  console.log('restored selection:', await api('/api/settings/themes/selection'));
  await close();
}
