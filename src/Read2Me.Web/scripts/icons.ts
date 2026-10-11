// `bun run icons`: rebuilds the subset icon font and icons.manifest.json from the IconName union
// (src/app/ui/icons.ts). It needs the network and runs only when the union changes.
// `bun run icons:check` (--check): offline; fails when the manifest differs from the union.
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { ICON_NAMES } from '../src/app/ui/icons';
import { manifestProblems, sortIconNames, subsetCssUrl, woff2Url } from './icon-manifest';

const manifestPath = fileURLToPath(new URL('../icons.manifest.json', import.meta.url));
const fontPath = fileURLToPath(
  new URL('../src/assets/fonts/MaterialSymbolsRounded.woff2', import.meta.url),
);
// Google Fonts answers with one variable woff2 only to a browser that supports it.
const USER_AGENT =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36';

function check(): number {
  let manifest: unknown = null;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  } catch {
    // Missing or unparsable: reported as malformed below.
  }
  const problems = manifestProblems(ICON_NAMES, manifest);
  if (!existsSync(fontPath)) {
    problems.push('src/assets/fonts/MaterialSymbolsRounded.woff2 is missing');
  }
  if (problems.length === 0) {
    console.log(`icons: the manifest matches IconName (${ICON_NAMES.length} icons).`);
    return 0;
  }
  console.error(
    'icons: the subset font is stale against IconName (src/app/ui/icons.ts).\n' +
      problems.map((problem) => `  ${problem}\n`).join('') +
      'Run `bun run icons` and commit the font and icons.manifest.json.',
  );
  return 1;
}

async function download(url: string): Promise<Response> {
  const res = await fetch(url, { headers: { 'user-agent': USER_AGENT } });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} from ${url}`);
  return res;
}

async function rebuild(): Promise<void> {
  const names = sortIconNames(ICON_NAMES);
  const css = await (await download(subsetCssUrl(names))).text();
  const font = new Uint8Array(await (await download(woff2Url(css))).arrayBuffer());
  if (new TextDecoder().decode(font.subarray(0, 4)) !== 'wOF2') {
    throw new Error('the downloaded font is not a woff2');
  }
  await Bun.write(fontPath, font);
  await Bun.write(manifestPath, `${JSON.stringify(names, null, 2)}\n`);
  console.log(`icons: wrote ${names.length} icons, ${font.byteLength} bytes.`);
}

if (process.argv.includes('--check')) process.exit(check());
await rebuild();
