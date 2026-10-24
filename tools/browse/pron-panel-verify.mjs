// Pronunciation ticket 16: the panel (rows, hover-light, editor from a row, a mark's pencil, the pill), the Activity swap.
import { launch, throwawayProject, openWeb, post, waitForServer, stamp } from './browse.mjs';

await waitForServer('http://localhost:5000/api/projects');
const { page, close } = await launch();
const project = await throwawayProject(
  'I\n\nPug ran to the keep. Kulgan waited.\n\n"Pug!" called Tomas.\n\nThe rain kept falling on Crydee.',
);
const log = (...a) => console.log(stamp(), ...a);
const shot = (name) => page.screenshot({ path: `pron-panel-${name}.png` });
try {
  await post(`/api/projects/${project.folder}/pronunciations`, { writtenForm: 'Pug', spokenForm: 'PUHG' });
  await post(`/api/projects/${project.folder}/pronunciations`, { writtenForm: 'Kulgan', spokenForm: 'KULL-gun' });
  await page.setViewportSize({ width: 1440, height: 900 });
  await openWeb(page, `/projects/${project.folder}/book?mode=speakers`);
  await page.waitForFunction(() => document.querySelectorAll('.r2m-pron-mark').length >= 3, null, { timeout: 15_000 });

  await page.locator('[data-action=toggle-pronunciations]').click();
  const panel = page.locator('r2m-pronunciation-panel');
  await panel.locator('[data-role=row]').first().waitFor();
  log('rows', JSON.stringify(await panel.locator('[data-role=row]').allInnerTexts()));
  await panel.locator('[data-role=row]').first().hover();
  log('lit while hovering Pug', await page.locator('.r2m-pron-mark--lit').count());
  await shot('1-rows-hover');

  await panel.locator('[data-role=row]').nth(1).locator('button').click();
  await panel.locator('r2m-pronunciation-editor').waitFor();
  await page.mouse.move(0, 0);
  await page.locator('r2m-pronunciation-sentence mark').waitFor({ timeout: 5_000 });
  log('editor written', await panel.locator('[data-field=written]').inputValue(), 'sentence', JSON.stringify(await page.locator('r2m-pronunciation-sentence').innerText()));
  log('lit with Kulgan open', JSON.stringify(await page.locator('.r2m-pron-mark--lit').allInnerTexts()));
  await shot('2-editor');

  // A mark's pencil opens it in the panel.
  await page.locator('.r2m-pron-mark', { hasText: 'Pug' }).first().hover();
  const card = page.locator('.pron-card__panel');
  await card.waitFor({ state: 'visible' });
  await card.locator('[data-action=edit-pronunciation]').click();
  await page.waitForFunction(() => document.querySelector('r2m-pronunciation-panel [data-field=written]')?.value === 'Pug');
  log('pencil opened Pug in the panel');

  // Select "Crydee": the pill offers Add, and Add pre-fills.
  await panel.locator('[data-action=cancel]').click();
  const text = page.locator('.r2m-item__text', { hasText: 'Crydee' });
  await text.evaluate((el) => {
    const node = [...el.childNodes].find((n) => n.nodeType === 3 && n.data.includes('Crydee'));
    const range = document.createRange();
    const at = node.data.indexOf('Crydee');
    range.setStart(node, at);
    range.setEnd(node, at + 'Crydee'.length);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  });
  const pill = page.locator('[data-action=pill]');
  await pill.waitFor({ state: 'visible' });
  log('pill', JSON.stringify(await pill.innerText()), 'popover-open', await page.locator('.pron-pill__popover').evaluate((e) => e.matches(':popover-open')));
  await shot('3-pill');
  await pill.click();
  await page.waitForFunction(() => document.querySelector('r2m-pronunciation-panel [data-field=written]')?.value === 'Crydee');
  await panel.locator('[data-field=spoken]').fill('CRY-dee');
  log('add pre-filled; spoken typed');

  // Activity takes the space; the draft is there when the panel comes back.
  await page.locator('r2m-activity-bar button').last().click();
  await page.waitForSelector('.shell__drawer');
  log('panel hidden after Activity opened', await panel.isHidden());
  await page.locator('[data-action=toggle-pronunciations]').click();
  log('drawer gone', (await page.locator('.shell__drawer').count()) === 0, 'spoken kept', await panel.locator('[data-field=spoken]').inputValue());
  await shot('4-draft-kept');

  // Docked beside the tree on a wide page; floating with the tree open below about 1100 px of page.
  for (const width of [1440, 1280, 1000]) {
    await page.setViewportSize({ width, height: 800 });
    await page.waitForFunction(() => document.querySelector('r2m-pronunciation-panel')?.getBoundingClientRect().width > 0);
    const layout = await page.evaluate(() => ({
      position: getComputedStyle(document.querySelector('r2m-pronunciation-panel')).position,
      reader: Math.round(document.querySelector('.book__reader').getBoundingClientRect().width),
    }));
    log(width, 'panel', layout.position, 'reader width', layout.reader);
  }
  await shot('5-narrow');
  log('errors', JSON.stringify(page.errors), 'toasts', JSON.stringify(page.toasts));
} finally {
  await project.remove();
  await close();
}
