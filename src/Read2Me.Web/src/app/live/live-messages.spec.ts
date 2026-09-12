import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  BOOK_FACETS,
  LIVE_FAMILIES,
  LIVE_HUB_METHODS,
  LIVE_KINDS,
  hasFacet,
} from './live-messages';

/**
 * Pins the TypeScript contract to the C# it mirrors. The C# side has no shared constants — the
 * family names are `[HubMethodName]` attributes and the `kind` strings are literals in the mapper —
 * so the test reads those sources (the web workspace is a sibling of Read2Me.App) and fails when
 * either side moves. Vitest runs under Node even with the jsdom environment, so `node:fs` is fine.
 */
const LIVE_DIR = resolve(process.cwd(), '../Read2Me.App/Live');

function source(file: string): string {
  return readFileSync(resolve(LIVE_DIR, file), 'utf8');
}

function all(text: string, pattern: RegExp): string[] {
  return Array.from(text.matchAll(pattern), (m) => m[1] ?? '');
}

function sorted(values: readonly string[]): string[] {
  return [...values].sort((a, b) => a.localeCompare(b, 'en'));
}

describe('live-messages contract', () => {
  it('lists exactly the hub families ILiveClient.cs pins with [HubMethodName]', () => {
    const csharp = all(source('ILiveClient.cs'), /\[HubMethodName\("([a-zA-Z]+)"\)\]/g);
    expect(sorted(LIVE_FAMILIES)).toEqual(sorted(csharp));
  });

  it('names the client → server methods LiveHub.cs declares', () => {
    const hub = source('LiveHub.cs');
    for (const method of Object.values(LIVE_HUB_METHODS)) {
      expect(hub, `LiveHub.cs has no public method ${method}`).toMatch(
        new RegExp(`public\\s+(?:async\\s+)?[\\w<>]+\\s+${method}\\(`),
      );
    }
  });

  it.each(Object.keys(LIVE_KINDS) as (keyof typeof LIVE_KINDS)[])(
    'pins the %s kind discriminators to LiveMessageMapper.cs',
    (family) => {
      const record = `${family[0]?.toUpperCase()}${family.slice(1)}Message`;
      const mapper = source('LiveMessageMapper.cs');
      // `new AssemblyMessage("phaseStarted"` and the `new("delta", …)` shorthand in the Delta helper.
      const explicit = all(mapper, new RegExp(`new ${record}\\("([a-zA-Z]+)"`, 'g'));
      const shorthand =
        family === 'llm'
          ? all(mapper, /public static LlmMessage Delta[^;]*new\("([a-zA-Z]+)"/g)
          : [];
      const csharp = [...new Set([...explicit, ...shorthand])];
      expect(csharp.length, `no ${record} constructions found in the mapper`).toBeGreaterThan(0);
      expect(sorted(LIVE_KINDS[family])).toEqual(sorted(csharp));
    },
  );

  it('pins the BookFacets flag names to BookMutationEffects.cs', () => {
    const effects = readFileSync(
      resolve(process.cwd(), '../Read2Me.Services/Mutations/BookMutationEffects.cs'),
      'utf8',
    );
    const enumBody = /enum BookFacets\s*\{([\s\S]*?)\n\}/.exec(effects)?.[1] ?? '';
    const names = all(enumBody, /^\s*([A-Z][A-Za-z]*)\s*=\s*1 << \d+/gm);
    expect(sorted(BOOK_FACETS)).toEqual(sorted(names));
  });
});

describe('hasFacet', () => {
  it('reads the comma-joined [Flags] string the server writes', () => {
    expect(hasFacet('Structure, Audio', 'Audio')).toBe(true);
    expect(hasFacet('Structure, Audio', 'Voices')).toBe(false);
    expect(hasFacet('Attribution', 'Attribution')).toBe(true);
    expect(hasFacet('None', 'Structure')).toBe(false);
    expect(hasFacet('All', 'NodeTitle')).toBe(true);
  });
});
