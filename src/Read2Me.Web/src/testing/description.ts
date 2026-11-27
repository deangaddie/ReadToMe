/** The text an element's `aria-describedby` resolves to in its tree, mounted in the document or not. */
export function description(el: Element): string | undefined {
  const id = el.getAttribute('aria-describedby');
  if (!id) return undefined;
  const root = el.getRootNode() as Element | Document;
  return root.querySelector(`#${id}`)?.textContent ?? undefined;
}

/** Whether anything under `host` carries a description, or the hidden element that would hold one. */
export const hasDescription = (host: ParentNode): boolean =>
  host.querySelector('[aria-describedby], .visually-hidden') !== null;
