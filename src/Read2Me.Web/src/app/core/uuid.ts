/**
 * A random (v4) UUID that works on any origin. `crypto.randomUUID` exists only in a secure context
 * (HTTPS or localhost), so a page served over plain HTTP by address would fail at module load;
 * `crypto.getRandomValues` exists everywhere, and the fallback builds the same v4 shape from it.
 */
export function newUuid(
  source: Pick<Crypto, 'getRandomValues'> & Partial<Pick<Crypto, 'randomUUID'>> = crypto,
): string {
  if (typeof source.randomUUID === 'function') return source.randomUUID();
  const b = source.getRandomValues(new Uint8Array(16));
  b[6] = (b[6]! & 0x0f) | 0x40; // version 4
  b[8] = (b[8]! & 0x3f) | 0x80; // RFC 9562 variant
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
