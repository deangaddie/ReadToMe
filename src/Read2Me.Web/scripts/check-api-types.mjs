// Fails when src/app/api/schema.d.ts is stale against the running host's /openapi/v1.json.
// Skips with a warning when no host answers, so `npm run check` still works offline.
// Regenerate with `npm run api:types`.
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const url = process.env.R2M_OPENAPI_URL ?? 'http://localhost:5000/openapi/v1.json';
const checkedIn = fileURLToPath(new URL('../src/app/api/schema.d.ts', import.meta.url));

let reachable = false;
try {
  const res = await fetch(url, { signal: AbortSignal.timeout(3000) });
  reachable = res.ok;
} catch {
  reachable = false;
}
if (!reachable) {
  console.warn(`check-api-types: ${url} not reachable, skipping (schema.d.ts not verified).`);
  process.exit(0);
}

// The package's exports map rewrites `./*.js` to `.mjs`, so resolve the package root, not the bin.
const pkg = createRequire(import.meta.url).resolve('openapi-typescript/package.json');
const cli = join(dirname(pkg), 'bin', 'cli.js');
const dir = mkdtempSync(join(tmpdir(), 'r2m-api-types-'));
const fresh = join(dir, 'schema.d.ts');
try {
  execFileSync(process.execPath, [cli, url, '-o', fresh], { stdio: ['ignore', 'ignore', 'inherit'] });
  const normalise = (text) => text.replace(/\r\n/g, '\n');
  if (normalise(readFileSync(fresh, 'utf8')) !== normalise(readFileSync(checkedIn, 'utf8'))) {
    console.error(
      'check-api-types: src/app/api/schema.d.ts is stale against the running host.\n' +
        'Run `npm run api:types`, review the diff, update dtos.ts / schema-pins.ts, and commit.',
    );
    process.exit(1);
  }
  console.log('check-api-types: schema.d.ts matches the running host.');
} finally {
  rmSync(dir, { recursive: true, force: true });
}
