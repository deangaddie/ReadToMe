/**
 * The token the built `index.html` carries as the `nonce` of every `<script>` and
 * `<link rel="modulepreload">`. The host (`AppPage.NoncePlaceholder`) replaces it with a fresh nonce on each response and sends
 * the matching Content-Security-Policy (docs/agents/api/README.md, "Request isolation and security
 * headers"). Bun's HTML bundler writes those tags itself and drops a `nonce` from the source, so
 * `build.ts` stamps them after bundling. The dev server serves the source page with no CSP.
 */
export const CSP_NONCE_PLACEHOLDER = '__CSP_NONCE__';

/** Sets the placeholder nonce on every script and modulepreload in `html`; `stamped` counts them. */
export function stampNonce(html: string): { html: string; stamped: number } {
  let stamped = 0;
  const stamp = (el: HTMLRewriterTypes.Element) => {
    el.setAttribute('nonce', CSP_NONCE_PLACEHOLDER);
    stamped++;
  };
  const out = new HTMLRewriter()
    .on('script', { element: stamp })
    .on('link[rel="modulepreload"]', { element: stamp })
    .transform(html);
  return { html: out, stamped };
}
