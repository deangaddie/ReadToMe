import { describe, expect, it } from 'bun:test';
import { BASE } from './base';

describe('BASE', () => {
  it('is an absolute folder path', () => {
    expect(BASE).toMatch(/^\/[a-z0-9]+\/$/);
  });

  // The dev server serves index.html as written, so its <base> cannot follow BASE at run time.
  it('is what the source index.html declares as its <base href>', async () => {
    const html = await Bun.file(new URL('./index.html', import.meta.url)).text();
    expect(html).toContain(`<base href="${BASE}"`);
  });
});
