// PROTOTYPE screenshots for .scratch/project-history/issues/06-history-panel-prototype.md
import { mkdirSync } from 'node:fs';
import { api, launch, openWeb, sleep, waitForServer } from './browse.mjs';
const out = '../../.scratch/project-history/assets/06-history-panel';
try { mkdirSync(out, { recursive: true }); } catch {}
await waitForServer('http://localhost:5000/api/projects');
const projects = await api('/api/projects');
const folder = projects.find((p) => p.folderName === 'dune')?.folderName ?? projects[0].folderName;
console.log('project', folder);
const { page, close } = await launch();
const sizes = { laptop: { width: 1366, height: 768 }, phone: { width: 390, height: 844 } };
const shot = async (name) => {
  await sleep(350);
  await page.screenshot({ path: `${out}/${name}.png` });
};
try {
  for (const [size, vp] of Object.entries(sizes)) {
    await page.setViewportSize(vp);
    // A: sheet → view a step → restore → toast
    await openWeb(page, `/projects/${folder}/book?variant=A`);
    await page.locator('button[aria-label="History"]').dispatchEvent('click');
    await shot(`A-${size}-1-sheet`);
    await page.locator('.proto-h__row', { hasText: 'Split chapter' }).dispatchEvent('click');
    await shot(`A-${size}-2-viewing`);
    await page.locator('.proto-h__banner button', { hasText: 'Restore this step' }).dispatchEvent('click');
    await shot(`A-${size}-3-restored`);

    // B: page → select → confirm → view read-only
    await openWeb(page, `/projects/${folder}/book?variant=B`);
    await page.locator('button[aria-label="History"]').dispatchEvent('click');
    await page.locator('.proto-h__table tr', { hasText: 'Split chapter' }).dispatchEvent('click');
    await shot(`B-${size}-1-page`);
    await page.locator('.proto-h__detail button', { hasText: 'Restore…' }).dispatchEvent('click');
    await shot(`B-${size}-2-confirm`);
    await page.locator('.proto-h__dialog button', { hasText: 'Cancel' }).dispatchEvent('click');
    await page.locator('.proto-h__detail button', { hasText: 'Open read-only' }).dispatchEvent('click');
    await shot(`B-${size}-3-viewing`);

    // C: strip → click a tick → inline confirm → restored
    await openWeb(page, `/projects/${folder}/book?variant=C`);
    await shot(`C-${size}-1-strip`);
    await page.locator('.proto-h__tick[aria-label="#8 Split chapter"]').dispatchEvent('click');
    await shot(`C-${size}-2-viewing`);
    await page.locator('.proto-h__card button', { hasText: 'Restore to here' }).dispatchEvent('click');
    await shot(`C-${size}-3-confirm`);
    await page.locator('.proto-h__card button', { hasText: 'Confirm' }).dispatchEvent('click');
    await shot(`C-${size}-4-restored`);
  }
  console.log('errors', page.errors);
} finally {
  await close();
}
