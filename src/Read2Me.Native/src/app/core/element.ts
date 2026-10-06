import { render } from 'lit-html';
import { effect } from './signals';

/**
 * Base class for every Read2Me element (light DOM). `template()` returns lit-html; one effect per
 * element tracks every signal it reads and re-renders at most once per microtask. Effects created
 * with {@link R2mElement.effect} live while the element is connected — the place for Angular's
 * constructor `effect()`s. Inputs are plain getter/setter pairs over a private signal; outputs are
 * DOM events ({@link R2mElement.emit}).
 */
export abstract class R2mElement extends HTMLElement {
  #disposers: (() => void)[] = [];
  #pending: unknown;
  #scheduled = false;

  protected abstract template(): unknown;

  /** Called on connect, before the first render: register effects and listeners here. */
  protected connected(): void {}

  /** Called after each render reached the DOM (Angular's `afterRender`): measure here. */
  protected updated(): void {}

  connectedCallback(): void {
    this.connected();
    this.effect(() => {
      this.#pending = this.template();
      this.#schedule();
    });
  }

  disconnectedCallback(): void {
    for (const dispose of this.#disposers.splice(0)) dispose();
  }

  protected effect(run: Parameters<typeof effect>[0]): void {
    this.#disposers.push(effect(run));
  }

  /** Runs `teardown` on disconnect (subscriptions, listeners). */
  protected onDisconnect(teardown: () => void): void {
    this.#disposers.push(teardown);
  }

  /**
   * Dispatches an output event on this element. It reaches listeners on the element itself; pass
   * `bubbles` only when an ancestor further up is the one listening.
   */
  protected emit<T>(type: string, detail?: T, options: { bubbles?: boolean } = {}): void {
    this.dispatchEvent(new CustomEvent(type, { detail, bubbles: options.bubbles ?? false }));
  }

  /** Resolves after pending renders flushed (tests, and focus-after-render). */
  rendered(): Promise<void> {
    return new Promise((resolve) => queueMicrotask(() => queueMicrotask(resolve)));
  }

  #schedule(): void {
    if (this.#scheduled) return;
    this.#scheduled = true;
    queueMicrotask(() => {
      this.#scheduled = false;
      if (!this.isConnected) return;
      render(this.#pending, this, { host: this });
      this.updated();
    });
  }
}

/**
 * `customElements.define` that survives the dev server's hot reload: redefining a tag throws, so
 * a re-executed element module reloads the page instead.
 */
export function define(tag: string, ctor: CustomElementConstructor): void {
  if (customElements.get(tag)) {
    if (import.meta.hot) location.reload();
    return;
  }
  customElements.define(tag, ctor);
}
