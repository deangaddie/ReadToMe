/**
 * The text an element's `aria-describedby` resolves to in its tree, mounted in the document or not:
 * each id's text in order, joined by a space, as a screen reader reads them.
 */
export function description(el: Element): string | undefined {
  const ids = el.getAttribute('aria-describedby')?.split(/\s+/).filter(Boolean) ?? [];
  if (ids.length === 0) return undefined;
  const root = el.getRootNode() as Element | Document;
  const texts = ids.flatMap((id) => root.querySelector(`#${id}`)?.textContent ?? []);
  return texts.length ? texts.join(' ') : undefined;
}

/** Whether anything under `host` carries a description, or the hidden element that would hold one. */
export const hasDescription = (host: ParentNode): boolean =>
  host.querySelector('[aria-describedby], .visually-hidden') !== null;

/**
 * The Blocked reason a gated control gives while it stays focusable (`el`, or the control inside
 * it): its description while `aria-disabled`, else null, as for an open or a plain disabled one.
 */
export function blockedWith(el: Element | null | undefined): string | null {
  const off = '[aria-disabled="true"]';
  const control = el?.matches(off) ? el : el?.querySelector(off);
  return (control && description(control)) ?? null;
}

/** Whether a control is off either way: plain `disabled`, or `aria-disabled` with a Blocked reason. */
export const isOff = (control: Element): boolean =>
  control.matches('[disabled], [aria-disabled="true"]');
