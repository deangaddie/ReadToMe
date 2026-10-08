// Scroll-jump probe for the checkpoint (native-web 28). Scrolls the measured list down and then up
// in fixed steps. Each step waits until the list settles (row set and row tops unchanged over two
// frames, chapter loads included), then reports how far the row at the viewport top moved beyond
// the requested delta (0 = no jump). Steps that would hit either end are skipped. Read-only.
//   MSYS_NO_PATHCONV=1 R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun native-scroll-jump.mjs dune 150 > jump.log 2>&1
import { launch, openWeb } from './browse.mjs';

const folder = process.argv[2] ?? 'dune';
const stepsPerDirection = Number(process.argv[3] ?? 150);
const stepPx = 400;
const { page, close } = await launch();

// One step: the tracked row's offset from where the requested delta should have put it, or null
// when the step would clamp at either end.
const step = (delta) =>
  page.evaluate(async (delta) => {
    const list = document.querySelector('r2m-measured-list');
    const frame = () => new Promise((r) => requestAnimationFrame(() => r()));
    const rowTops = () => {
      const listTop = list.getBoundingClientRect().top;
      return new Map(
        [...list.querySelectorAll('.r2m-vlist__row')].map((r) => [r.dataset.rowKey, r.getBoundingClientRect().top - listTop]),
      );
    };
    const signature = () => `${list.items.length}|${[...rowTops()].map(([k, t]) => `${k}:${t.toFixed(2)}`).join(',')}`;
    const settle = async () => {
      let last = '';
      let quietFrames = 0;
      for (let i = 0; i < 300 && quietFrames < 2; i++) {
        await frame();
        const now = signature();
        quietFrames = now === last ? quietFrames + 1 : 0;
        last = now;
      }
    };

    await settle();
    const target = list.scrollTop + delta;
    if (target < 0 || target > list.scrollHeight - list.clientHeight) return null;
    const before = rowTops();
    const [anchorKey, anchorTop] = [...before].find(([, top]) => top >= 0) ?? [];
    list.scrollTop = target;
    await settle();
    const after = rowTops();
    return anchorKey && after.has(anchorKey) ? after.get(anchorKey) - (anchorTop - delta) : null;
  }, delta);

try {
  await openWeb(page, `/projects/${folder}/book`);
  await page.waitForSelector('r2m-book-page .book__chapter', { timeout: 15000 });
  const jumps = [];
  let skipped = 0;
  for (const delta of [stepPx, -stepPx]) {
    for (let i = 0; i < stepsPerDirection; i++) {
      const jump = await step(delta);
      if (jump == null) skipped++;
      else jumps.push(Math.abs(jump));
    }
  }
  if (jumps.length === 0) throw new Error('no samples: every step clamped or lost its anchor row');
  jumps.sort((a, b) => a - b);
  const over = (px) => jumps.filter((j) => j > px).length;
  console.log(
    JSON.stringify({
      folder,
      samples: jumps.length,
      skipped,
      max: +jumps.at(-1).toFixed(2),
      p99: +jumps[Math.floor(jumps.length * 0.99)].toFixed(2),
      over0_5px: over(0.5),
      over1px: over(1),
    }),
  );
  const big = jumps.filter((j) => j > 1);
  if (big.length) console.log('jumps > 1px:', big.map((j) => j.toFixed(1)).join(' '));
  console.log('errors', page.errors, 'toasts', page.toasts);
} finally {
  await close();
}
