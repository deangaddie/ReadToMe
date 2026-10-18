// `bun run help`: renders docs/guide/<step>.md (one file per Pipeline step, README.md ignored)
// into src/app/help/help.generated.ts, failing on a missing or extra step file and on unsafe
// markdown (see help-render.ts). `bun run help:check` (--check): fails when the module is stale.
// Authoring loop, from the repo root: `bun --watch src/Read2Me.Web/scripts/help.ts`. The pages are
// imported as text so that they join the module graph the watcher tracks, and the watcher only sees
// files under its working directory, hence the repo root (docs/guide is outside this package).
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PIPELINE_STEP_IDS as STEP_IDS } from '../src/app/api/dtos';
import { type HelpEntry, helpModuleSource, renderHelp } from './help-render';

const guideDir = fileURLToPath(new URL('../../../docs/guide/', import.meta.url));
const modulePath = fileURLToPath(new URL('../src/app/help/help.generated.ts', import.meta.url));

async function build(): Promise<string> {
  const present = readdirSync(guideDir)
    .filter((name) => name.endsWith('.md') && name !== 'README.md')
    .map((name) => basename(name, '.md'));
  const extra = present.filter((id) => !(STEP_IDS as readonly string[]).includes(id));
  const missing = STEP_IDS.filter((id) => !present.includes(id));
  if (extra.length > 0 || missing.length > 0) {
    throw new Error(
      'help: docs/guide must hold exactly one <step>.md per Pipeline step.' +
        (missing.length > 0 ? `\n  missing: ${missing.join(', ')}` : '') +
        (extra.length > 0 ? `\n  not a step: ${extra.join(', ')}` : ''),
    );
  }
  const entries: Record<string, HelpEntry> = {};
  for (const id of STEP_IDS) {
    const file = `docs/guide/${id}.md`;
    const page = (await import(join(guideDir, `${id}.md`), { with: { type: 'text' } })) as {
      default: string;
    };
    entries[id] = renderHelp(page.default, file);
  }
  return helpModuleSource(entries);
}

function check(fresh: string): number {
  const current = existsSync(modulePath) ? readFileSync(modulePath, 'utf8') : '';
  if (current === fresh) {
    console.log(
      `help: src/app/help/help.generated.ts matches docs/guide (${STEP_IDS.length} steps).`,
    );
    return 0;
  }
  console.error(
    'help: src/app/help/help.generated.ts is stale against docs/guide/*.md.\n' +
      'Run `bun run help` and commit the module.',
  );
  return 1;
}

let fresh: string;
try {
  fresh = await build();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}
if (process.argv.includes('--check')) process.exit(check(fresh));
await Bun.write(modulePath, fresh);
console.log(`help: wrote ${STEP_IDS.length} steps to src/app/help/help.generated.ts.`);
