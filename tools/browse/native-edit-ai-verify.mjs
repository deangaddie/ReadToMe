// Verifies native-web 27 against the host's /app2 bundle: the Book actions menu carries "Edit
// with AI…" with the auto_fix_high icon, and the entry opens r2m-edit-with-ai-dialog full screen on
// the Instruct phase with the AI-activity toggle at the bottom. Read-only: no AI call is made.
//   MSYS_NO_PATHCONV=1 R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun native-edit-ai-verify.mjs
import { launch, openWeb } from './browse.mjs';

const folder = process.argv[2] ?? 'alices-adventures-in-wonderland';
const { page, close } = await launch();

try {
  await openWeb(page, `/projects/${folder}/book`);
  await page.waitForSelector('r2m-book-page .book__chapter', { timeout: 15000 });

  await page.locator('[data-testid=book-actions]').click();
  const entry = page.getByRole('menuitem', { name: 'Edit with AI…' });
  await entry.waitFor({ state: 'visible' });
  console.log('menu entry icon:', await entry.locator('.r2m-icon').textContent());
  await page.screenshot({ path: 'edit-ai-menu.png' });
  await entry.click();

  const dialog = page.locator('r2m-edit-with-ai-dialog');
  await dialog.waitFor({ state: 'visible' });
  const facts = await page.evaluate(() => {
    const d = document.querySelector('dialog.r2m-dialog');
    const r = d.getBoundingClientRect();
    return {
      dialogClass: d.className,
      open: d.open,
      size: `${Math.round(r.width)}x${Math.round(r.height)} of ${innerWidth}x${innerHeight}`,
      phase: d.querySelector('.edit').dataset.phase,
      analyzeDisabled: d.querySelector('[data-action=analyze]').disabled,
      streamToggle: d.querySelector('[data-action=toggle-stream]')?.textContent.trim(),
      focused: document.activeElement?.tagName,
    };
  });
  console.log('dialog:', JSON.stringify(facts));
  await dialog.locator('[data-testid=instruction]').fill('rename every chapter');
  console.log('analyze enabled after typing:', await dialog.locator('[data-action=analyze]').isEnabled());
  await page.screenshot({ path: 'edit-ai-dialog.png' });

  await dialog.locator('[data-action=cancel]').click();
  await dialog.waitFor({ state: 'detached' });
  console.log('closed; errors:', JSON.stringify(page.errors), 'toasts:', JSON.stringify(page.toasts));
} finally {
  await close();
}
