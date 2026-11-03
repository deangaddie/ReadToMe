// #220: the series cast board on the Projects page — Share as new, Link (with gains), Unlink, and
// Remove from series naming the linked Character. Two Holmes books (Gutenberg) as a throwaway series.
import { launch, bookProject, openWeb, acceptConfirm, api, post, waitFor } from './browse.mjs';

const { page, close } = await launch();
const scarlet = await bookProject('a-study-in-scarlet');
const four = await bookProject('the-sign-of-the-four');
let seriesId;
const cmd = (folder, body) =>
  api(`/api/projects/${folder}/commands`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
try {
  const holmes1 = (await cmd(scarlet.folder, { type: 'CreateCharacter', name: 'Sherlock Holmes' })).newEntityId;
  await cmd(scarlet.folder, { type: 'CreateVoice', characterId: holmes1, name: 'Deerstalker' });
  const holmes2 = (await cmd(four.folder, { type: 'CreateCharacter', name: 'Holmes' })).newEntityId;
  await cmd(four.folder, { type: 'CreateVoice', characterId: holmes2, name: 'Violin' });
  seriesId = (await post('/api/series', { name: `Holmes verify ${Date.now().toString(36)}`, folder: scarlet.folder })).id;
  await api(`/api/series/${seriesId}/books/${four.folder}`, {
    method: 'PUT', headers: { 'content-type': 'application/json' }, body: '{}',
  });

  await openWeb(page, '/projects');
  await page.click('.projects__view input[value="series"]');
  await page.click(`.projects__series-card[data-series-id="${seriesId}"] .projects__series-open`);
  await page.click('[role=tab][data-tab="characters"]');
  await page.waitForSelector('r2m-series-cast-board .series-cast__table');
  console.log('empty board:', (await page.locator('r2m-series-cast-board').innerText()).replace(/\s+/g, ' '));

  // Share as new from Book 1.
  await page.selectOption(`td[data-cell="share"][data-folder="${scarlet.folder}"] select`, holmes1);
  console.log('share confirm:', await acceptConfirm(page));
  await page.waitForSelector('tr[data-series-character]');

  // Link Book 2's Holmes from the empty cell.
  await page.selectOption(`tr[data-series-character] td[data-folder="${four.folder}"][data-cell="empty"] select`, holmes2);
  console.log('link confirm:', await acceptConfirm(page));
  await page.waitForSelector(`tr[data-series-character] td[data-folder="${four.folder}"][data-cell="linked"]`);
  const voices = await api(`/api/projects/${four.folder}/characters/${holmes2}/voices`);
  console.log('Book 2 Holmes voices:', voices.voices.map((v) => v.name));
  await page.screenshot({ path: 'series-cast-verify.png', fullPage: true });

  // Remove from series confirm names the linked Character (cancel it).
  await page.click('.projects__view input[value="books"]');
  const card = page.locator(`.r2m-project-card[data-folder="${four.folder}"]`);
  await card.locator('.r2m-project-card__more').click();
  await card.locator('[data-action="leave-series"]').click();
  await page.waitForSelector('r2m-confirm-dialog', { state: 'visible' });
  console.log('leave confirm:', (await page.locator('r2m-confirm-dialog').innerText()).replace(/\s+/g, ' '));
  await page.click('r2m-confirm-dialog .r2m-confirm-dialog__cancel');
  await page.waitForSelector('r2m-confirm-dialog', { state: 'detached' });

  // Unlink Book 2 with the ✕.
  await page.click('.projects__view input[value="series"]');
  await page.click('[role=tab][data-tab="characters"]').catch(async () => {
    await page.click(`.projects__series-card[data-series-id="${seriesId}"] .projects__series-open`);
    await page.click('[role=tab][data-tab="characters"]');
  });
  await page.click(`td[data-folder="${four.folder}"][data-cell="linked"] [data-action="unlink"]`);
  console.log('unlink confirm:', await acceptConfirm(page));
  await page.waitForSelector(`tr[data-series-character] td[data-folder="${four.folder}"][data-cell="empty"]`);
  console.log('after unlink, Book 2 keeps:', (await api(`/api/projects/${four.folder}/characters/${holmes2}/voices`)).voices.map((v) => v.name));
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  if (seriesId) await fetch(`http://localhost:5000/api/series/${seriesId}`, { method: 'DELETE' });
  await scarlet.remove();
  await four.remove();
  await close();
}
