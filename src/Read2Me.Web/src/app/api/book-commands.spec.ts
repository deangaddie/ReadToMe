import { BOOK_COMMAND_TYPES } from './book-commands';

// Same default as scripts/check-api-types.mjs; there is no env plumbing in the browser test bundle.
const HOST = 'http://localhost:5000';

/**
 * Round-trips the hand-written command union against the host's own list. The commands endpoint
 * answers 400 to an unknown `type` with "Known types: A, B, …" (BookCommandJson.cs). Unknown folders
 * 404 before the type is parsed, so the probe needs an existing project; without a reachable host or
 * a project the test skips with a note rather than failing.
 */
describe('BOOK_COMMAND_TYPES', () => {
  it('is sorted and free of duplicates', () => {
    const sorted = [...BOOK_COMMAND_TYPES].sort((a, b) => a.localeCompare(b, 'en'));
    expect([...BOOK_COMMAND_TYPES]).toEqual(sorted);
    expect(new Set(BOOK_COMMAND_TYPES).size).toBe(BOOK_COMMAND_TYPES.length);
  });

  it('matches the "Known types" list of the running host', async ({ skip }) => {
    const folder = await firstProjectFolder();
    if (folder === null) {
      skip(`${HOST} not reachable or has no projects; command types not verified against host`);
      return;
    }
    const res = await fetch(`${HOST}/api/projects/${encodeURIComponent(folder)}/commands`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ type: '__probe__' }),
    });
    expect(res.status).toBe(400);
    const problem = (await res.json()) as { detail?: string };
    const match = /Known types: (.*)\.$/.exec(problem.detail ?? '');
    expect(match, `unexpected 400 body: ${problem.detail}`).not.toBeNull();
    const hostTypes = (match?.[1] ?? '')
      .split(',')
      .map((t) => t.trim())
      .sort((a, b) => a.localeCompare(b, 'en'));
    expect([...BOOK_COMMAND_TYPES]).toEqual(hostTypes);
  });
});

async function firstProjectFolder(): Promise<string | null> {
  try {
    const res = await fetch(`${HOST}/api/projects`, { signal: AbortSignal.timeout(2000) });
    if (!res.ok) return null;
    const projects = (await res.json()) as { folderName: string }[];
    return projects[0]?.folderName ?? null;
  } catch {
    return null;
  }
}
