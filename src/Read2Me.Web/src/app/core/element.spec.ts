import { afterEach, describe, expect, it } from 'bun:test';
import { html, nothing } from 'lit-html';
import { R2mElement, define } from './element';
import { signal } from './signals';

class Greeter extends R2mElement {
  #name = signal('world');
  get name() {
    return this.#name();
  }
  set name(value: string) {
    this.#name.set(value);
  }

  readonly ticks = signal(0);
  log: string[] = [];
  updates = 0;

  protected override connected(): void {
    this.log.push('connected');
    this.effect((onCleanup) => {
      this.log.push(`effect ${this.ticks()}`);
      onCleanup(() => this.log.push('effect cleanup'));
    });
    this.onDisconnect(() => this.log.push('teardown'));
  }

  protected template() {
    return html`<p>Hello ${this.#name()}</p>`;
  }

  protected override updated(): void {
    this.updates++;
  }

  announce(bubbles: boolean): void {
    this.emit('r2m-hello', this.name, { bubbles });
  }
}
define('x-greeter', Greeter);

function mount(): Greeter {
  const el = document.createElement('x-greeter') as Greeter;
  document.body.append(el);
  return el;
}

afterEach(() => document.body.replaceChildren());

describe('R2mElement', () => {
  it('renders its template into the light DOM after connecting', async () => {
    const el = mount();
    expect(el.shadowRoot).toBeNull();
    await el.rendered();
    expect(el.querySelector('p')?.textContent).toBe('Hello world');
  });

  it('re-renders when an input changes', async () => {
    const el = mount();
    await el.rendered();
    el.name = 'Hardin';
    await el.rendered();
    expect(el.querySelector('p')?.textContent).toBe('Hello Hardin');
  });

  it('takes inputs set before it is connected', async () => {
    const el = document.createElement('x-greeter') as Greeter;
    el.name = 'Gaal';
    document.body.append(el);
    await el.rendered();
    expect(el.querySelector('p')?.textContent).toBe('Hello Gaal');
  });

  it('coalesces several writes in one task into one render', async () => {
    const el = mount();
    await el.rendered();
    const before = el.updates;
    el.name = 'a';
    el.name = 'b';
    el.name = 'c';
    await el.rendered();
    expect(el.updates).toBe(before + 1);
    expect(el.querySelector('p')?.textContent).toBe('Hello c');
  });

  it('calls updated() after each render reached the DOM', async () => {
    const el = mount();
    expect(el.updates).toBe(0);
    await el.rendered();
    expect(el.updates).toBe(1);
  });

  it('runs connected() before the first render, and effects while connected', async () => {
    const el = mount();
    await el.rendered();
    el.ticks.set(1);
    expect(el.log).toEqual(['connected', 'effect 0', 'effect cleanup', 'effect 1']);
  });

  it('disposes effects and teardowns on disconnect, and stops rendering', async () => {
    const el = mount();
    await el.rendered();
    el.log = [];
    el.remove();
    expect(el.log).toEqual(['effect cleanup', 'teardown']);

    const updates = el.updates;
    el.ticks.set(2);
    el.name = 'nobody';
    await el.rendered();
    expect(el.log).toEqual(['effect cleanup', 'teardown']);
    expect(el.updates).toBe(updates);
  });

  it('starts over when it is connected again', async () => {
    const el = mount();
    await el.rendered();
    el.remove();
    el.name = 'again';
    document.body.append(el);
    await el.rendered();
    expect(el.querySelector('p')?.textContent).toBe('Hello again');
  });

  it('emits an output as a DOM event that does not bubble unless asked to', () => {
    const el = mount();
    const onHost: unknown[] = [];
    const onBody: unknown[] = [];
    el.addEventListener('r2m-hello', (e) => onHost.push((e as CustomEvent).detail));
    document.body.addEventListener('r2m-hello', (e) => onBody.push((e as CustomEvent).detail));

    el.announce(false);
    expect(onHost).toEqual(['world']);
    expect(onBody).toEqual([]);

    el.announce(true);
    expect(onBody).toEqual(['world']);
  });
});

describe('define', () => {
  it('registers the element', () => {
    expect(customElements.get('x-greeter')).toBe(Greeter);
  });

  it('does not throw when the tag is already defined', () => {
    class Other extends R2mElement {
      protected template() {
        return nothing;
      }
    }
    expect(() => define('x-greeter', Other)).not.toThrow();
    expect(customElements.get('x-greeter')).toBe(Greeter);
  });
});
