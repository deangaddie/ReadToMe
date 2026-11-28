/**
 * True while an IME composes, when Enter confirms a conversion and Escape cancels one: a custom
 * Enter or Escape handler returns early on it. Safari ends the composition before the confirming
 * keydown, where only the key code 229 still tells.
 */
export function isComposing(e: KeyboardEvent): boolean {
  return e.isComposing || e.keyCode === 229;
}
