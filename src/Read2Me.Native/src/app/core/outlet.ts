import { R2mElement, define } from './element';
import { use } from './services';
import { Router } from './router';
import { untracked } from './signals';

/**
 * Renders one level of the matched route chain: the root outlet renders level 0, an outlet inside
 * that element level 1, and so on. Loads the level's chunk first; keeps the element while the tag
 * stays the same, so a param change is a signal change, not a re-mount.
 */
export class R2mOutlet extends R2mElement {
  #router = use(Router);
  #current: Element | null = null;

  protected override connected(): void {
    let depth = 0;
    for (let el = this.parentElement; el; el = el.parentElement)
      if (el instanceof R2mOutlet) depth++;
    this.effect(() => {
      const route = this.#router.match()?.chain[depth];
      untracked(() => void this.#show(route?.tag, route?.load));
    });
  }

  protected template(): unknown {
    return undefined;
  }

  async #show(tag: string | undefined, load: (() => Promise<unknown>) | undefined): Promise<void> {
    if (!tag) {
      this.#current?.remove();
      this.#current = null;
      return;
    }
    if (this.#current?.localName === tag) return;
    await load?.();
    if (this.#router.match()?.chain.some((r) => r.tag === tag) !== true) return;
    this.#current = document.createElement(tag);
    this.replaceChildren(this.#current);
  }
}
define('r2m-outlet', R2mOutlet);
