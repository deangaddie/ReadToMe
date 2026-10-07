// Verify the native activity centre and the preflight gate (native-web 21) against a real host.
//   R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun activity-check.mjs > activity-check.log 2>&1
import { launch, openWeb } from './browse.mjs';

const { page, close } = await launch();
try {
  await openWeb(page, '/projects/foundation/book');
  await page.waitForSelector('app-root .shell__brand');

  const bar = page.locator('r2m-activity-bar');
  console.log('bar:', (await bar.textContent()).trim());

  await page.getByLabel('Toggle activity drawer').click();
  await page.waitForSelector('r2m-activity-drawer');
  const tabs = await page.locator('r2m-activity-drawer [role="tab"]').allTextContents();
  console.log('tabs:', tabs.map((t) => t.trim().split(/\s+/).at(-1)));
  console.log('jobs tab:', (await page.locator('.activity-jobs').textContent()).trim().slice(0, 120));

  await page.getByRole('tab', { name: 'Services', exact: true }).click();
  await page.waitForSelector('.activity-services__row');
  const rows = page.locator('.activity-services__row');
  const n = await rows.count();
  const services = [];
  for (let i = 0; i < n; i++) {
    const row = rows.nth(i);
    services.push([
      await row.getAttribute('data-service'),
      (await row.locator('.r2m-status-chip').textContent()).trim().split(/\s+/).at(-1),
    ]);
  }
  console.log('services:', JSON.stringify(services));
  await page.screenshot({ path: 'activity-drawer.png' });

  await page.getByLabel('Close activity drawer').click();
  await page.waitForSelector('r2m-activity-drawer', { state: 'detached' });
  console.log('drawer closed:', (await page.locator('r2m-activity-drawer').count()) === 0);

  const gate = page.evaluate(() => window.r2m.preflight.ensureReady('attribution'));
  const sheet = page.locator('dialog.r2m-dialog--sheet r2m-preflight-sheet');
  const outcome = await Promise.race([
    gate.then((v) => `resolved:${v}`),
    sheet.waitFor({ timeout: 15000 }).then(() => 'sheet'),
  ]);
  console.log('preflight:', outcome);
  if (outcome === 'sheet') {
    console.log('sheet title:', await sheet.locator('h2').textContent());
    console.log('to start:', await sheet.locator('.r2m-preflight-sheet__name').allTextContents());
    await page.screenshot({ path: 'preflight-sheet.png' });
    await sheet.getByRole('button', { name: 'Cancel' }).click();
    console.log('after cancel:', await gate);
    console.log('sheet gone:', (await page.locator('dialog').count()) === 0);
  }
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await close();
}
