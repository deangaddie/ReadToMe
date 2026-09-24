// Ad-hoc verification of the cast page (Angular ticket 15) against a running host.
// R2M_WEB=http://localhost:5000 node cast-check.mjs > cast-check.log 2>&1
import { launch, throwawayProject, openWeb, answerPrompt, acceptConfirm, api, post, waitFor } from './browse.mjs';

const { page, close } = await launch();
const project = await throwawayProject(
  'Chapter 1\n\n"Hello there," said Alice.\n\n"Who goes there?" came the reply.\n\nIt was quiet.\n\n"Only me," she answered.',
);
const base = `/api/projects/${project.folder}`;
try {
  // Seed two characters and attribute one paragraph through the API.
  const alice = await post(`${base}/commands`, { type: 'CreateCharacter', name: 'Alice' });
  await post(`${base}/commands`, { type: 'CreateCharacter', name: 'Bob' });
  const book = await api(`${base}/book`);
  const chapters = await api(`${base}/nodes/volume/${book.volumes[0].id}/children`);
  const chapter = chapters.parts
    ? (await api(`${base}/nodes/part/${chapters.parts[0].id}/children`)).chapters[0]
    : chapters.chapters[0];
  const paragraphs = (await api(`${base}/nodes/chapter/${chapter.id}/children`)).paragraphs;
  const dialog = paragraphs.filter((p) => p.items.some((i) => i.itemType === 'Character'));
  await post(`${base}/commands`, { type: 'SetParagraphCharacter', paragraphId: dialog[0].id, characterId: alice.newEntityId });
  await post(`${base}/commands`, { type: 'SetParagraphCharacter', paragraphId: dialog[1].id, characterId: alice.newEntityId });

  await openWeb(page, `/projects/${project.folder}/cast`);
  await page.waitForSelector('.cast__row');
  console.log('rows:', await page.$$eval('.cast__row', (els) => els.map((e) => e.textContent.replace(/\s+/g, ' ').trim())));
  await page.screenshot({ path: 'cast-list.png', fullPage: true });

  // Select Alice: detail with aliases and lines.
  await page.locator(`.cast__row[data-character-id="${alice.newEntityId}"]`).click();
  await page.waitForSelector('app-character-detail');
  await page.waitForSelector('.character-lines__row');
  console.log('lines:', await page.$$eval('.character-lines__row', (els) => els.map((e) => e.textContent.trim())));

  // Expand the first line and load context.
  await page.locator('.character-lines__toggle').first().click();
  await page.locator('[data-action="load-context"]').click();
  await page.waitForSelector('.line-context__paragraph--query');
  console.log('context paragraphs:', await page.$$eval('.line-context__paragraph', (els) => els.length));
  console.log('context speakers:', await page.$$eval('.line-context__speakers', (els) => els.map((e) => e.textContent.replace(/\s+/g, ' ').trim())));
  await page.screenshot({ path: 'cast-detail.png', fullPage: true });

  // Add alias inline.
  await page.locator('[data-action="add-alias"]').click();
  await page.locator('.character-detail__alias-input').fill('Al');
  await page.locator('.character-detail__alias-input').press('Enter');
  await page.waitForSelector('.character-detail__alias[data-alias="Al"]');
  console.log('aliases after add:', await page.$$eval('.character-detail__alias', (els) => els.map((e) => e.textContent.trim())));

  // Rename via inline edit.
  await page.locator('app-character-detail .r2m-inline-edit__display').click();
  await page.locator('app-character-detail .r2m-inline-edit__input').fill('Alice Liddell');
  await page.locator('app-character-detail .r2m-inline-edit__input').press('Enter');
  await waitFor(async () => (await api(`${base}/characters/summary`)).some((c) => c.name === 'Alice Liddell'), 'rename persisted');
  await page.waitForSelector(`.cast__row[data-character-id="${alice.newEntityId}"]:has-text("Alice Liddell")`);
  console.log('row after rename:', (await page.locator(`.cast__row[data-character-id="${alice.newEntityId}"]`).textContent()).replace(/\s+/g, ' ').trim());

  // Add a character via the toolbar prompt; it becomes selected.
  await page.locator('[data-action="add-character"]').click();
  await answerPrompt(page, 'Carol');
  await waitFor(async () => (await page.locator('.cast__row--selected').textContent()).includes('Carol'), 'Carol selected');
  console.log('selected:', (await page.locator('.cast__row--selected').textContent()).trim());

  // Link the narrator to Alice via the banner select.
  await page.locator('app-narrator-banner mat-label').click();
  await page.waitForSelector('.mat-mdc-select-panel');
  await page.locator('mat-option', { hasText: 'Alice Liddell' }).click();
  await waitFor(async () => (await api(`${base}`)).narrator.isLinked, 'narrator linked');
  await page.waitForSelector('app-narrator-banner .narrator-banner__unlink');
  await page.waitForSelector('.cast__row:has-text("Narrator → Alice Liddell")');
  console.log('banner:', (await page.locator('app-narrator-banner').textContent()).replace(/\s+/g, ' ').trim());
  console.log('narrator row:', (await page.locator('.cast__row').first().textContent()).replace(/\s+/g, ' ').trim());

  // Selecting Narrator now shows the signpost.
  await page.locator('.cast__row').first().click();
  await page.waitForSelector('app-narrator-signpost');
  console.log('signpost:', (await page.locator('app-narrator-signpost').textContent()).replace(/\s+/g, ' ').trim());
  await page.screenshot({ path: 'cast-signpost.png', fullPage: true });

  // Merge Carol into Bob, then delete Bob.
  const summary = await api(`${base}/characters/summary`);
  const carol = summary.find((c) => c.name === 'Carol');
  const bob = summary.find((c) => c.name === 'Bob');
  await page.locator(`.cast__row[data-character-id="${carol.id}"]`).click();
  await page.waitForSelector('app-character-detail:has-text("Carol")');
  await page.locator('[data-action="merge"]').click();
  await page.waitForSelector('app-merge-dialog');
  await page.screenshot({ path: 'cast-merge.png' });
  await page.locator('app-merge-dialog mat-label').click();
  await page.waitForSelector('.mat-mdc-select-panel');
  console.log('merge options:', await page.$$eval('.mat-mdc-select-panel mat-option', (els) => els.map((e) => e.textContent.trim())));
  await page.locator('.mat-mdc-select-panel mat-option', { hasText: 'Bob' }).click();
  await page.locator('app-merge-dialog .merge__submit').click();
  await waitFor(async () => !(await api(`${base}/characters/summary`)).some((c) => c.name === 'Carol'), 'merged');
  await page.waitForURL(new RegExp(`/cast/${bob.id}$`));
  console.log('after merge url:', page.url());
  console.log('bob aliases:', (await api(`${base}/characters/summary`)).find((c) => c.id === bob.id).aliases.map((a) => a.name));
  await page.waitForSelector('app-character-detail:has-text("Bob")');
  await page.locator('[data-action="delete"]').click();
  console.log('confirm:', await acceptConfirm(page));
  await waitFor(async () => !(await api(`${base}/characters/summary`)).some((c) => c.id === bob.id), 'deleted');
  await page.waitForURL(/\/cast$/);
  console.log('after delete url:', page.url());
  console.log('summary after delete:', (await api(`${base}/characters/summary`)).map((c) => c.name));
  await waitFor(async () => (await page.$$eval('.cast__row', (els) => els.length)) === 2, 'roster shrank');
  console.log('final rows:', await page.$$eval('.cast__row', (els) => els.map((e) => e.textContent.replace(/\s+/g, ' ').trim())));

  // Line click opens the reader in speakers mode.
  await page.locator(`.cast__row[data-character-id="${alice.newEntityId}"]`).click();
  await page.waitForSelector('.character-lines__text');
  await page.locator('.character-lines__text').first().click();
  await page.waitForURL(/\/book\?/);
  console.log('reader url:', page.url());

  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await project.remove();
  await close();
}
