// PROTOTYPE screenshots for .scratch/guidance-primitives/issues/03-overview-prototype.md
import { launch, openWeb, sleep } from './browse.mjs';
const out = '../../.scratch/guidance-primitives/assets/03-overview';
const { page, close } = await launch();
// The shell scrolls inside <main>, so "full" views use a tall viewport instead of fullPage.
const sizes = {
  laptop: { width: 1366, height: 768 },
  'laptop-full': { width: 1366, height: 1900 },
  phone: { width: 390, height: 844 },
  'phone-full': { width: 390, height: 2600 },
};
try {
  for (const v of ['A', 'B', 'C']) {
    for (const [name, vp] of Object.entries(sizes)) {
      await page.setViewportSize(vp);
      await openWeb(page, `/projects/alice-in-wonderland?variant=${v}`);
      await page.waitForSelector('.proto-pipeline');
      await sleep(400);
      await page.screenshot({ path: `${out}/${v}-${name}.png` });
      await page.locator('[data-step="structure"] .proto-help-toggle').click();
      await sleep(300);
      await page.screenshot({ path: `${out}/${v}-${name}-help.png` });
      if (v === 'C') {
        await page.locator('[data-step="titles"] .proto-row-toggle').click();
        await sleep(300);
        await page.screenshot({ path: `${out}/${v}-${name}-titles.png` });
      }
    }
  }
  console.log('errors', page.errors);
} finally {
  await close();
}
