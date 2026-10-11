// Smoke check: the app serves its shell for a project route. Prints the brand, the page title and
// what the route rendered (an unknown project path still renders the shell).
//   R2M_WEB=http://localhost:5000 bun shell-check.mjs > shell-check.log 2>&1
import { launch, openWeb, APP, WEB, visibleText } from './browse.mjs';

const { page, close } = await launch();
try {
  await openWeb(page, '/projects/foundation/book');
  await page.waitForSelector('app-root .shell__brand');
  console.log('app:', APP, 'at', WEB, '→', page.url());
  console.log('brand:', (await page.locator('app-root .shell__brand').first().textContent()).trim());
  console.log('title:', await page.title());
  console.log('not found:', (await page.locator('r2m-not-found-page').count()) ? 'yes' : 'no');
  console.log('text:', (await visibleText(page)).slice(0, 300));
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await close();
}
