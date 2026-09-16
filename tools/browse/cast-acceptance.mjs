// Ticket 15 acceptance: web cast edits show in Blazor beside it; a linked narrator shows
// "Narrator → X" lines in the reader's Audio mode.
// R2M_WEB=http://localhost:5000 node cast-acceptance.mjs > cast-acceptance.log 2>&1
import { launch, throwawayProject, openWeb, openBlazor, acceptConfirm, api, post, waitFor } from './browse.mjs';

const { page, close } = await launch();
const project = await throwawayProject(
  'Chapter 1\n\n"Hello there," said Alice.\n\n"Who goes there?" came the reply.\n\nIt was quiet.\n\n"Only me," she answered.',
);
const base = `/api/projects/${project.folder}`;
try {
  const alice = (await post(`${base}/commands`, { type: 'CreateCharacter', name: 'Alice' })).newEntityId;
  const bob = (await post(`${base}/commands`, { type: 'CreateCharacter', name: 'Bob' })).newEntityId;
  const carol = (await post(`${base}/commands`, { type: 'CreateCharacter', name: 'Carol' })).newEntityId;
  await post(`${base}/commands`, { type: 'AddCharacterAlias', characterId: alice, name: 'Al' });
  const book = await api(`${base}/book`);
  const vol = await api(`${base}/nodes/volume/${book.volumes[0].id}/children`);
  const chapter = vol.parts ? (await api(`${base}/nodes/part/${vol.parts[0].id}/children`)).chapters[0] : vol.chapters[0];
  const paragraphs = (await api(`${base}/nodes/chapter/${chapter.id}/children`)).paragraphs;
  const dialog = paragraphs.filter((p) => p.items.some((i) => i.itemType === 'Character'));
  await post(`${base}/commands`, { type: 'SetParagraphCharacter', paragraphId: dialog[0].id, characterId: alice });

  // --- Web edits: rename, alias add + remove, merge, delete, link narrator.
  await openWeb(page, `/projects/${project.folder}/cast/${alice}`);
  await page.waitForSelector('app-character-detail');
  await page.locator('app-character-detail .r2m-inline-edit__display').click();
  await page.locator('app-character-detail .r2m-inline-edit__input').fill('Alice Liddell');
  await page.locator('app-character-detail .r2m-inline-edit__input').press('Enter');
  await page.waitForSelector('.cast__row:has-text("Alice Liddell")');

  await page.locator('[data-action="add-alias"]').click();
  await page.locator('.character-detail__alias-input').fill('Ally');
  await page.locator('.character-detail__alias-input').press('Enter');
  await page.waitForSelector('.character-detail__alias[data-alias="Ally"]');
  await page.locator('.character-detail__alias[data-alias="Al"] button').click();
  await page.waitForSelector('.character-detail__alias[data-alias="Al"]', { state: 'detached' });

  await page.locator(`.cast__row[data-character-id="${carol}"]`).click();
  await page.waitForSelector('app-character-detail:has-text("Carol")');
  await page.locator('[data-action="merge"]').click();
  await page.waitForSelector('app-merge-dialog');
  await page.locator('app-merge-dialog mat-label').click();
  await page.waitForSelector('.mat-mdc-select-panel');
  await page.locator('.mat-mdc-select-panel mat-option', { hasText: 'Bob' }).click();
  await page.locator('app-merge-dialog .merge__submit').click();
  await page.waitForURL(new RegExp(`/cast/${bob}$`));
  await page.waitForSelector('app-character-detail:has-text("Bob")');
  await page.locator('[data-action="delete"]').click();
  await acceptConfirm(page);
  await page.waitForURL(/\/cast$/);

  await page.locator('app-narrator-banner mat-label').click();
  await page.waitForSelector('.mat-mdc-select-panel');
  await page.locator('mat-option', { hasText: 'Alice Liddell' }).click();
  await page.waitForSelector('app-narrator-banner .narrator-banner__unlink');
  console.log('web summary:', (await api(`${base}/characters/summary`)).map((c) => `${c.name}[${c.aliases.map((a) => a.name)}]${c.narratesBook ? '*' : ''}`));

  // --- Blazor beside it: Characters tab reflects every edit.
  await openBlazor(page, `/project/${project.folder}`);
  await page.locator('.mud-tab', { hasText: 'Characters' }).click();
  await page.waitForSelector('[data-testid="narrator-link-banner"]');
  await waitFor(async () => (await page.locator('[data-testid="narrator-link-banner"]').innerText()).includes('Alice Liddell'), 'blazor banner');
  const blazorList = await page.locator('.mud-list').first().innerText();
  console.log('blazor banner:', (await page.locator('[data-testid="narrator-link-banner"]').innerText()).replace(/\s+/g, ' '));
  console.log('blazor list:', blazorList.replace(/\s+/g, ' '));
  await page.locator('.mud-list-item', { hasText: 'Alice Liddell' }).filter({ hasNotText: 'Narrator' }).first().click();
  await waitFor(async () => (await page.innerText('body')).includes('Ally'), 'blazor alias');
  const body = await page.innerText('body');
  console.log('blazor has Ally:', body.includes('Ally'), '| has stale Al chip:', /\bAl\b(?! ice)/.test(body.replace('Alice Liddell', '')), '| has Bob:', body.includes('Bob'), '| has Carol:', body.includes('Carol'));

  // --- Reader Audio mode shows "Narrator → Alice Liddell" on narration lines.
  await openWeb(page, `/projects/${project.folder}/book?mode=audio`);
  await waitFor(async () => (await page.innerText('body')).includes('Narrator → Alice Liddell'), 'audio mode narrator label', 15_000);
  const count = await page.locator('r2m-item:has-text("Narrator → Alice Liddell")').count();
  console.log('audio-mode narrator lines:', count);
  await page.screenshot({ path: 'cast-audio-mode.png', fullPage: true });

  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await project.remove();
  await close();
}
