// #222: differs from series. Two Holmes books (Gutenberg) share Watson; Book 2 is Restored to before
// a Voice rename, so it differs. Checks the board and roster badges, the editor's callout and push,
// and the Cast page's take, with their confirms.
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
const text = async (sel) => (await page.locator(sel).first().innerText()).replace(/\s+/g, ' ');
try {
  const w1 = (await cmd(scarlet.folder, { type: 'CreateCharacter', name: 'Watson' })).newEntityId;
  const w2 = (await cmd(four.folder, { type: 'CreateCharacter', name: 'Watson' })).newEntityId;
  seriesId = (await post('/api/series', { name: `Holmes differs ${Date.now().toString(36)}`, folder: scarlet.folder })).id;
  await api(`/api/series/${seriesId}/books/${four.folder}`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: '{}' });
  const sc = (await post(`/api/series/${seriesId}/characters`, { folder: scarlet.folder, characterId: w1 })).seriesCharacterId;
  await api(`/api/series/${seriesId}/characters/${sc}/links/${four.folder}`, {
    method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ characterId: w2 }),
  });
  // Book 2 gains a Voice "Pipe" with a positional rule; Book 1 is Restored to before it, so the tie
  // makes Book 1 the series copy and Book 2 differs (a take there loses "Pipe" and its rule).
  await cmd(scarlet.folder, { type: 'CreateVoice', characterId: w1, name: 'Army doctor' });
  const before = (await api(`/api/projects/${scarlet.folder}/history`)).headStepId;
  const pipe = (await cmd(four.folder, { type: 'CreateVoice', characterId: w2, name: 'Pipe' })).newEntityId;
  const ch = (await api(`/api/projects/${four.folder}/book`)).volumes?.[0];
  console.log('rule:', await cmd(four.folder, { type: 'CreateVoiceRule', characterId: w2, voiceId: pipe, fromLevel: null, fromNodeId: null, toLevel: null, toNodeId: null }));
  await api(`/api/projects/${scarlet.folder}/history/${before}/restore`, { method: 'POST' });

  // Board: the differs badge on Book 2's cell; with two books, the tie gives Book 1 the series copy.
  await openWeb(page, `/projects?series=${seriesId}&character=${sc}`);
  await page.waitForSelector(`td[data-folder="${four.folder}"] .series-cast__differs`);
  console.log('board badge on Book 2:', true,
    '| on Book 1:', await page.locator(`td[data-folder="${scarlet.folder}"] .series-cast__differs`).count());

  // The editor's callout and its push (cancelled: we take on the Cast page instead).
  await page.waitForSelector('[data-testid=differs-callout]');
  console.log('editor callout:', await text('[data-testid=differs-callout]'));
  await page.screenshot({ path: 'series-differs-board.png', fullPage: true });
  await page.click('[data-testid=differs-callout] [data-action=push]');
  await page.waitForSelector('r2m-confirm-dialog', { state: 'visible' });
  console.log('push confirm:', await text('r2m-confirm-dialog'));
  await page.click('r2m-confirm-dialog .r2m-confirm-dialog__cancel');

  // The Cast page: roster badge and callout; take with its confirm.
  await openWeb(page, `/projects/${four.folder}/cast/${w2}`);
  await page.waitForSelector(`.cast__row[data-character-id="${w2}"] [data-testid=differs-badge]`);
  console.log('roster differs badge:', true);
  await page.waitForSelector('r2m-voices-section [data-testid=differs-callout]');
  console.log('cast callout:', await text('r2m-voices-section [data-testid=differs-callout]'));
  await page.screenshot({ path: 'series-differs-cast.png', fullPage: true });
  await page.click('r2m-voices-section [data-action=take]');
  console.log('take confirm:', await acceptConfirm(page));
  await page.waitForSelector('r2m-voices-section [data-testid=differs-callout]', { state: 'detached' });
  await page.waitForSelector(`.cast__row[data-character-id="${w2}"] [data-testid=differs-badge]`, { state: 'detached' });
  const after = await api(`/api/projects/${four.folder}/characters/${w2}/voices`);
  console.log('after take, Book 2 voices:', after.voices.map((v) => v.name),
    '| history head:', (await api(`/api/projects/${four.folder}/history`)).steps[0].label);
  const cast = await api(`/api/series/${seriesId}/cast`);
  console.log('board differs after:', cast.rows[0].cells.map((c) => c.linked?.differs));
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  if (seriesId) await fetch(`http://localhost:5000/api/series/${seriesId}`, { method: 'DELETE' });
  await scarlet.remove();
  await four.remove();
  await close();
}
