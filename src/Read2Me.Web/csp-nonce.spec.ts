import { describe, expect, it } from 'bun:test';
import { CSP_NONCE_PLACEHOLDER, stampNonce } from './csp-nonce';

const built =
  '<head><link rel="stylesheet" href="/app/a.css"><script type="module" src="/app/a.js"></script>' +
  '<link rel="modulepreload" href="/app/b.js"><link rel="icon" href="/app/f.ico"></head>' +
  '<body><script>1</script></body>';

describe('stampNonce', () => {
  it('puts the placeholder on every script and modulepreload, and nothing else', () => {
    const { html, stamped } = stampNonce(built);

    expect(stamped).toBe(3);
    expect(html).toContain(
      `<script type="module" src="/app/a.js" nonce="${CSP_NONCE_PLACEHOLDER}">`,
    );
    expect(html).toContain(
      `<link rel="modulepreload" href="/app/b.js" nonce="${CSP_NONCE_PLACEHOLDER}">`,
    );
    expect(html).toContain(`<script nonce="${CSP_NONCE_PLACEHOLDER}">1</script>`);
    expect(html).toContain('<link rel="stylesheet" href="/app/a.css">');
    expect(html).toContain('<link rel="icon" href="/app/f.ico">');
  });

  it('replaces a nonce already written, so the placeholder is the only one', () => {
    const { html } = stampNonce('<script nonce="abc" src="/app/a.js"></script>');

    expect(html).toBe(`<script nonce="${CSP_NONCE_PLACEHOLDER}" src="/app/a.js"></script>`);
  });
});
