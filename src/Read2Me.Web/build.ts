import { mkdirSync, rmSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { BASE } from './base';

// Two Bun 1.4.2 Windows bugs: rmSync throws EINVAL for a relative path through `..`, whether or not
// it exists (so the path is made absolute), and Bun.build does not create its outdir.
const here = import.meta.dir;
const outdir = resolve(
  process.argv[2] ?? join(here, `../Read2Me.App/wwwroot${BASE.replace(/\/$/, '')}`),
);
rmSync(outdir, { recursive: true, force: true });
mkdirSync(outdir, { recursive: true });

const result = await Bun.build({
  entrypoints: [join(here, 'index.html')],
  outdir,
  target: 'browser',
  splitting: true,
  minify: true,
  sourcemap: 'linked',
  publicPath: BASE,
  naming: { chunk: '[name]-[hash].[ext]', asset: '[name]-[hash].[ext]' },
});
if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}

const indexPath = join(outdir, 'index.html');
const BASE_TAG = /<base href="[^"]*"\s*\/?>/;
const html = await Bun.file(indexPath).text();
if (!BASE_TAG.test(html)) {
  console.error(`build: ${indexPath} has no <base href> to set`);
  process.exit(1);
}
await Bun.write(indexPath, html.replace(BASE_TAG, `<base href="${BASE}">`));

for (const o of result.outputs) console.log(o.kind.padEnd(12), o.path);
