// Pronunciation ticket 15: marks in every Reader mode, the hover card, Edit → ?entry=, and a save elsewhere.
import { launch, throwawayProject, openWeb, post, waitForServer, stamp, WEB } from './browse.mjs';

await waitForServer('http://localhost:5000/api/projects');
const { page, close } = await launch();
const project = await throwawayProject(
  'I\n\nHolmes ran to the door. Watson waited.\n\n"Holmes!" called Lestrade.\n\nThe rain kept falling.',
);
const log = (...a) => console.log(stamp(), ...a);
try {
  await post(`/api/projects/${project.folder}/pronunciations`, { writtenForm: 'Holmes', spokenForm: 'HOHMZ' });
  for (const mode of ['read', 'speakers', 'audio']) {
    await openWeb(page, `/projects/${project.folder}/book?mode=${mode}`);
    await page.waitForFunction(() => document.querySelectorAll('.r2m-pron-mark').length >= 2, null, { timeout: 15_000 });
    const marks = await page.$$eval('.r2m-pron-mark', (els) => els.map((e) => e.textContent));
    const style = await page.$eval('.r2m-pron-mark', (e) => getComputedStyle(e).textDecorationStyle);
    log(mode, 'marks', JSON.stringify(marks), 'decoration', style);
  }
  await page.screenshot({ path: 'pron-marks-audio.png' });

  await openWeb(page, `/projects/${project.folder}/book?mode=speakers`);
  const mark = page.locator('.r2m-pron-mark').first();
  await mark.waitFor();
  await mark.hover();
  const card = page.locator('.pron-card__panel');
  await card.waitFor({ state: 'visible' });
  log('card', JSON.stringify(await card.innerText()), 'popover-open', await card.evaluate((e) => e.matches(':popover-open')));
  const play = card.locator('[data-action=play-sentence]');
  log('play disabled', await play.isDisabled(), 'reason', await play.evaluate((b) => b.closest('.r2m-blockable')?.dataset.tooltip ?? null));
  await page.screenshot({ path: 'pron-marks-card.png' });

  // A save elsewhere: the open view gains a mark.
  await post(`/api/projects/${project.folder}/pronunciations`, { writtenForm: 'Watson', spokenForm: 'WAT-sun' });
  await page.waitForFunction(() => [...document.querySelectorAll('.r2m-pron-mark')].some((m) => m.textContent === 'Watson'), null, { timeout: 5_000 });
  log('Watson marked after save elsewhere');

  // Edit pencil opens the entry on the Pronunciations page.
  await page.locator('.r2m-pron-mark', { hasText: 'Holmes' }).first().hover();
  await card.waitFor({ state: 'visible' });
  await card.locator('[data-action=edit-pronunciation]').click();
  await page.waitForSelector('r2m-pronunciation-editor');
  log('url', page.url().replace(WEB, ''), 'editor written', await page.$eval('[data-field=written]', (i) => i.value));
  log('errors', JSON.stringify(page.errors), 'toasts', JSON.stringify(page.toasts));
} finally {
  await project.remove();
  await close();
}
