/**
 * Adds a module's CSS to the document when the module loads. Element modules import their CSS as
 * text and call this, because Bun emits a lazy chunk's `import './x.css'` as a separate file that
 * the production HTML never links (the dev server injects it, so only the build shows the gap).
 */
export function adoptStyles(cssText: string): void {
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(cssText);
  document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
}
