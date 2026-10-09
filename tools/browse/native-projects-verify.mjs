// Verifies the native projects shelf and project overview (native-web 29) against a running host:
// the shelf's cards (cover or initials, author, progress), the New project dialog opening and
// closing, then one project's overview with its six pipeline steps and the details panel.
// Read-only: nothing is created or changed.
//   R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun native-projects-verify.mjs <folder> > native-projects-verify.log 2>&1
import { launch, openWeb } from './browse.mjs';

const folder = process.argv[2] ?? 'foundation';
const { page, close } = await launch();
try {
  await openWeb(page, '/projects');
  await page.waitForSelector('r2m-projects-page .r2m-project-card');
  const cards = page.locator('.r2m-project-card');
  const summary = await cards.evaluateAll((els) =>
    els.map((el) => ({
      folder: el.dataset.folder,
      title: el.querySelector('.r2m-project-card__title')?.textContent.trim(),
      author: el.querySelector('.r2m-project-card__author')?.textContent.trim(),
      cover: el.querySelector('.r2m-project-card__image') ? 'image' : el.querySelector('.r2m-project-card__initials')?.textContent.trim(),
      progress: el.querySelector('.r2m-project-card__percent')?.textContent.trim() ?? el.querySelector('.r2m-project-card__unread')?.textContent.trim(),
    })),
  );
  console.log('shelf cards:', summary.length);
  for (const c of summary) console.log(' ', JSON.stringify(c));
  console.log('placeholder on shelf:', (await page.locator('r2m-placeholder-page').count()) ? 'yes' : 'no');

  // The card menu opens as a popover under its trigger.
  const first = cards.first();
  await first.locator('.r2m-project-card__more').click();
  const menu = first.locator('[role=menu]');
  await menu.waitFor({ state: 'visible' });
  const menuBox = await menu.boundingBox();
  const moreBox = await first.locator('.r2m-project-card__more').boundingBox();
  console.log('card menu items:', await menu.locator('[role=menuitem]').allTextContents(), 'below trigger:', menuBox.y >= moreBox.y + moreBox.height - 1);
  await page.keyboard.press('Escape');
  await menu.waitFor({ state: 'hidden' });

  // The New project dialog opens, fills the project title from the book title, then is cancelled.
  await page.locator('.projects__new').first().click();
  const dialog = page.locator('r2m-new-project-dialog');
  await dialog.waitFor({ state: 'visible' });
  console.log('dialog focused field:', await page.evaluate(() => document.activeElement?.getAttribute('name')));
  await dialog.locator('input[name=bookTitle]').fill('Probe Title');
  console.log('project title follows:', await dialog.locator('input[name=title]').inputValue());
  console.log('create disabled:', await dialog.locator('.new-project__create').isDisabled());
  await dialog.locator('.new-project__cancel').click();
  await dialog.waitFor({ state: 'hidden' });
  await page.screenshot({ path: 'native-projects-shelf.png' });

  // Open a project's overview through its card.
  await page.locator(`.r2m-project-card[data-folder="${folder}"] .r2m-project-card__cover`).click();
  await page.waitForSelector('r2m-overview-page .r2m-pipeline__step');
  console.log('overview url:', page.url());
  console.log('header:', (await page.locator('r2m-overview-page .r2m-page-header__title').textContent()).trim());
  const steps = await page.locator('.r2m-pipeline__step').evaluateAll((els) =>
    els.map((el) => ({
      step: el.dataset.step,
      next: el.getAttribute('aria-current') === 'step',
      done: el.classList.contains('r2m-pipeline__step--done'),
      chip: el.querySelector('.r2m-pipeline__chip')?.textContent.replace(/\s+/g, ' ').trim(),
      detail: el.querySelector('.r2m-pipeline__detail')?.textContent.trim(),
      actions: [...el.querySelectorAll('[data-action]')].map((b) => `${b.dataset.action}${b.disabled ? '(off)' : ''}`),
    })),
  );
  for (const s of steps) console.log(' ', JSON.stringify(s));
  const details = page.locator('r2m-project-details-panel');
  console.log('details:', (await details.innerText()).replace(/\s+/g, ' ').slice(0, 400));
  console.log('cover shown:', await details.locator('.details__image').count(), 'switch checked:', await details.locator('input[name=narratorOnly]').isChecked());
  console.log('crumbs:', await page.locator('.shell__crumb').allTextContents(), 'title:', await page.title());
  await page.screenshot({ path: 'native-projects-overview.png', fullPage: true });
  console.log('toasts:', page.toasts, 'errors:', page.errors);
} finally {
  await close();
}
