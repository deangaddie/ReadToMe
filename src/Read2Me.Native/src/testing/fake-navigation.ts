/**
 * happy-dom has no Navigation API, so router-backed specs install this stand-in. It keeps a
 * history list, fires `navigate` with the fields the router reads, and commits the URL when the
 * event was intercepted and not cancelled — what the browser does.
 */
export interface FakeNavigateInit {
  navigationType?: NavigationType;
  canIntercept?: boolean;
  hashChange?: boolean;
  cancelable?: boolean;
}

export class FakeNavigation extends EventTarget {
  /** Committed URLs, oldest first; the entry's index is its history key. */
  readonly entries: string[] = [location.href];
  /** Every `navigate()` call, as the router made it. */
  readonly calls: { url: string; history: string | undefined }[] = [];
  /** URLs the browser would have loaded itself, because nothing intercepted them. */
  readonly passedThrough: string[] = [];
  /** The options of every `intercept()` the router made, oldest first. */
  readonly intercepts: { url: string; focusReset?: string; scroll?: string }[] = [];
  #current = 0;

  navigate(url: string, options: { history?: 'auto' | 'push' | 'replace' } = {}): void {
    const href = new URL(url, document.baseURI).href;
    this.calls.push({ url: href, history: options.history });
    this.fire(href, { navigationType: options.history === 'replace' ? 'replace' : 'push' });
  }

  traverseTo(key: string): void {
    const url = this.entries[Number(key)];
    if (url === undefined) throw new Error(`no history entry ${key}`);
    this.fire(url, { navigationType: 'traverse' }, Number(key));
  }

  /** A link click or the back button: a navigation the router did not start. */
  fire(url: string, init: FakeNavigateInit = {}, traverseIndex?: number): Event {
    const href = new URL(url, document.baseURI).href;
    const navigationType = init.navigationType ?? 'push';
    const key = navigationType === 'traverse' ? (traverseIndex ?? this.entries.indexOf(href)) : -1;
    let handler: (() => Promise<void>) | undefined;
    const event = Object.assign(new Event('navigate', { cancelable: init.cancelable ?? true }), {
      canIntercept: init.canIntercept ?? true,
      hashChange: init.hashChange ?? false,
      downloadRequest: null,
      formData: null,
      navigationType,
      destination: { url: href, key: String(key) },
      intercept: (options: { handler: () => Promise<void>; focusReset?: string; scroll?: string }) => {
        handler = options.handler;
        const { focusReset, scroll } = options;
        this.intercepts.push({
          url: href,
          ...(focusReset === undefined ? {} : { focusReset }),
          ...(scroll === undefined ? {} : { scroll }),
        });
      },
    });
    this.dispatchEvent(event);
    if (event.defaultPrevented) return event;
    if (!handler) {
      this.passedThrough.push(href);
      return event;
    }
    if (navigationType === 'traverse') this.#current = key;
    else if (navigationType === 'replace') this.entries[this.#current] = href;
    else {
      this.entries.splice(this.#current + 1, Infinity, href);
      this.#current++;
    }
    setUrl(href);
    void handler();
    return event;
  }
}

export function setUrl(url: string): void {
  (window as unknown as { happyDOM: { setURL(url: string): void } }).happyDOM.setURL(url);
}

/**
 * Points the document at `path` under a `<base href="/app2/">` and installs a fresh
 * {@link FakeNavigation} as the global `navigation`.
 */
export function installNavigation(path = ''): FakeNavigation {
  setUrl(`http://localhost/app2/${path}`);
  document.head.querySelector('base')?.remove();
  const base = document.createElement('base');
  base.setAttribute('href', '/app2/');
  document.head.append(base);
  const fake = new FakeNavigation();
  (globalThis as unknown as { navigation: FakeNavigation }).navigation = fake;
  return fake;
}

/** Lets intercepted handlers, lazy `load()`s and coalesced renders finish. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}
