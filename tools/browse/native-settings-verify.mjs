// Native settings LLM + Prompts (native-web 34): renders, token insert, preview, discard prompt.
// Read-only against the workspace: no edit is saved.
import { HOST, launch, openWeb, waitForServer } from './browse.mjs';

const shots = process.env.R2M_SHOTS ?? '.';
await waitForServer(`${HOST}/api/projects`);
const { page, close } = await launch();
const log = (...a) => console.log(...a);
try {
  await openWeb(page, '/settings/llm');
  await page.locator('r2m-llm-settings-page .r2m-config-list__row').first().waitFor();
  log('llm rows:', await page.locator('.r2m-config-list__name').allInnerTexts());
  log('editor title:', await page.locator('.r2m-config-editor-frame__title').innerText());
  log('labels:', await page.locator('r2m-llm-config-editor label.r2m-field__label').allInnerTexts());
  log('hints:', await page.locator('r2m-llm-config-editor .r2m-field__hint').count());
  log('describedBy batch:', await page.locator('#llm-batch-size').getAttribute('aria-describedby'));
  log('chain card:', await page.locator('r2m-llm-chain-card .chain__title').innerText());
  log('console:', (await page.locator('r2m-llm-test-console .console__title').count()) ? await page.locator('r2m-llm-test-console .console__title').innerText() : 'none (no default)');
  await page.screenshot({ path: `${shots}/native-settings-llm.png`, fullPage: true });

  await openWeb(page, '/settings/prompts');
  const template = page.locator('r2m-prompt-editor [data-field="template"]');
  await template.waitFor();
  log('prompt kinds:', await page.locator('.prompts__name').allInnerTexts());
  const original = await template.inputValue();
  await template.fill('Who speaks in ?');
  await template.evaluate((t) => t.setSelectionRange(14, 14));
  await page.locator('[data-token="book_title"]').click();
  log('after token:', await template.inputValue());
  await page.locator('.prompt-editor__preview > summary').click();
  await page.locator('[data-role="preview"]').waitFor({ timeout: 8000 });
  log('preview:', await page.locator('[data-role="preview"]').innerText());
  log('dirty shown:', await page.locator('.r2m-config-editor-frame__dirty').count());
  await page.screenshot({ path: `${shots}/native-settings-prompts.png`, fullPage: true });

  // Leaving with unsaved edits asks first; cancel keeps the draft.
  await page.locator('.shell__nav-item', { hasText: 'LLM' }).click();
  const dialog = page.locator('r2m-confirm-dialog');
  await dialog.waitFor({ timeout: 5000 });
  log('discard prompt:', await dialog.innerText());
  await dialog.locator('.r2m-confirm-dialog__cancel').click();
  log('still on prompts:', page.url().endsWith('/settings/prompts'), 'draft kept:', (await template.inputValue()) !== original);
  // Discard the draft without saving.
  await page.locator('.r2m-config-editor-frame__foot button', { hasText: 'Cancel' }).click();
  log('reverted:', (await template.inputValue()) === original);
  log('toasts/errors:', page.toasts ?? [], page.errors ?? []);
} finally {
  await close();
}
