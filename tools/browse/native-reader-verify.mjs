// Verifies native-web 22 against the host's /app2 bundle on a real workspace book: the reader
// renders in the measured list, modes switch and mirror the URL, the chapter window loads on
// scroll and caps at 5, the current chapter follows the top row, and anchoring holds over a
// prepend and a resize. Read-only: it mutates nothing.
//   MSYS_NO_PATHCONV=1 R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun native-reader-verify.mjs
import { launch, openWeb, waitFor } from './browse.mjs';

const folder = process.argv[2] ?? 'dune';
const { page, close } = await launch();

const state = () =>
  page.evaluate(() => {
    const list = document.querySelector('r2m-measured-list');
    const items = list.items;
    const chapters = items.filter((r) => r.kind === 'chapter').map((r) => r.title);
    const top = list.getBoundingClientRect().top;
    const rows = [...list.querySelectorAll('.r2m-vlist__row')].map((r) => ({
      key: r.dataset.rowKey,
      top: Math.round((r.getBoundingClientRect().top - top) * 100) / 100,
      height: Math.round(r.getBoundingClientRect().height * 100) / 100,
    }));
    return {
      chapters,
      rows: items.length,
      rendered: rows.length,
      scrollTop: list.scrollTop,
      scrollHeight: list.scrollHeight,
      clientHeight: list.clientHeight,
      current: document.querySelector('r2m-book-page').dataset.currentChapter,
      topRow: rows.find((r) => r.top <= 1 && r.top + r.height > 1),
    };
  });

try {
  await openWeb(page, `/projects/${folder}/book`);
  await page.waitForSelector('r2m-book-page .book__chapter', { timeout: 15000 });
  let s = await state();
  console.log('opened:', JSON.stringify(s));
  await page.screenshot({ path: 'reader-read.png' });

  // Modes.
  await page.getByRole('radiogroup', { name: 'Reader mode' }).getByText('Speakers').click();
  await waitFor(() => page.url().includes('mode=speakers'), 'url mode=speakers');
  await page.waitForSelector('.r2m-item .r2m-speaker-chip', { state: 'attached' });
  console.log('speakers: items', await page.locator('.r2m-item').count(), 'url', page.url());
  await page.screenshot({ path: 'reader-speakers.png' });
  await page.getByRole('radiogroup', { name: 'Reader mode' }).getByText('Audio').click();
  await page.waitForSelector('[data-testid=voice-line]', { state: 'attached' });
  console.log('audio: voice lines', await page.locator('[data-testid=voice-line]').count(), 'first', await page.locator('[data-testid=voice-line]').first().textContent());
  await page.screenshot({ path: 'reader-audio.png' });
  await page.getByRole('radiogroup', { name: 'Reader mode' }).getByText('Read').click();
  await waitFor(() => !page.url().includes('mode='), 'url without mode');

  // Scroll on through the book: the window loads next chapters and caps at 5.
  let max = 0;
  for (let i = 0; i < 40; i++) {
    await page.evaluate(() => {
      const list = document.querySelector('r2m-measured-list');
      list.scrollTop = list.scrollTop + list.clientHeight * 2;
      list.dispatchEvent(new Event('scroll'));
    });
    await page.waitForTimeout(300);
    s = await state();
    max = Math.max(max, s.chapters.length);
  }
  console.log('after scrolling: chapters', s.chapters.length, 'max seen', max, 'current', s.current, 'topRow', JSON.stringify(s.topRow));

  // Anchoring over a prepend: open a mid-book chapter by deep link, let the previous chapter load.
  const chapterId = await page.evaluate(() => {
    const list = document.querySelector('r2m-measured-list');
    return list.items.filter((r) => r.kind === 'chapter').at(-1).chapterId;
  });
  await openWeb(page, `/projects/${folder}/book?chapter=${chapterId}`);
  await page.waitForSelector(`[data-row-key="chapter:${chapterId}"]`, { state: 'attached', timeout: 15000 });
  await page.waitForTimeout(600);
  const before = await state();
  console.log('deep link: chapters', JSON.stringify(before.chapters), 'topRow', JSON.stringify(before.topRow), 'current', before.current);
  await waitFor(async () => (await state()).chapters.length >= 2, 'neighbours loaded', 15000);
  await page.waitForTimeout(500);
  const after = await state();
  console.log('after neighbours: chapters', JSON.stringify(after.chapters), 'topRow', JSON.stringify(after.topRow), 'current', after.current, 'scrollTop', after.scrollTop);

  // Resize: narrow then wide, the top row stays put.
  await page.evaluate(() => {
    const list = document.querySelector('r2m-measured-list');
    list.scrollTop = list.scrollTop + 900;
    list.dispatchEvent(new Event('scroll'));
  });
  await page.waitForTimeout(400);
  const mid = await state();
  await page.setViewportSize({ width: 800, height: 700 });
  await page.waitForTimeout(500);
  const narrow = await state();
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForTimeout(500);
  const wide = await state();
  console.log('resize: mid', JSON.stringify(mid.topRow), 'narrow', JSON.stringify(narrow.topRow), 'wide', JSON.stringify(wide.topRow));
  await page.screenshot({ path: 'reader-deep.png' });
  console.log('errors', page.errors, 'toasts', page.toasts);
} finally {
  await close();
}
