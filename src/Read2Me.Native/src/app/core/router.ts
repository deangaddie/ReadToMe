import { computed, effect, signal } from './signals';

/**
 * The Navigation-API router (replaces @angular/router). Routes nest like Angular's; each level
 * names the custom element that renders it and a lazy `load` for its chunk. `<r2m-outlet>`
 * renders its level of the matched chain. Params, query and titles are signals.
 *
 * Paths are app-relative: the app lives under the document's `<base href>`, and route paths,
 * `redirectTo` and {@link Router.navigate} are all written without it (`projects/x/book`).
 * Templates link the same way, with relative `href`s the browser resolves against the base.
 *
 * Unsaved-changes guard: when the leaf element says it holds unsaved edits, the navigation is
 * cancelled, `confirmLeave` asks, and a yes repeats it (a back/forward goes through `traverseTo`).
 */
export interface RouteDef {
  path: string;
  tag?: string;
  load?: () => Promise<unknown>;
  redirectTo?: string;
  /** Breadcrumb / document title; a function gets the route params. */
  title?: string | ((params: Record<string, string>) => string);
  children?: RouteDef[];
  /** The leaf element implements `hasUnsavedChanges()`. */
  guardUnsaved?: boolean;
}

export interface MatchedRoute {
  chain: RouteDef[];
  params: Record<string, string>;
}

interface FlatRoute {
  pattern: URLPattern;
  chain: RouteDef[];
}

export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

export class Router {
  #routes: FlatRoute[] = [];
  #bypassNext = false;
  #confirmLeave: () => Promise<boolean> = async () => true;
  /** The base path with its trailing slash, e.g. `/app2/`. */
  readonly #base = new URL(document.baseURI).pathname;

  readonly #url = signal(new URL(location.href));
  readonly url = this.#url.asReadonly();
  readonly match = computed<MatchedRoute | null>(() => this.#resolve(this.#url()));
  readonly params = computed(() => this.match()?.params ?? {});
  readonly query = computed(() => this.#url().searchParams);
  /** The current path below the base, without slashes at either end (`projects/x/book`). */
  readonly path = computed(() => this.#relative(this.#url())?.slice(1) ?? '');

  start(routes: RouteDef[], confirmLeave: () => Promise<boolean>): void {
    this.#routes = flatten(routes, '', []);
    this.#confirmLeave = confirmLeave;
    navigation.addEventListener('navigate', (e) => this.#onNavigate(e));
    // Closing the tab or leaving the app: the browser shows its own prompt.
    addEventListener('beforeunload', (e) => {
      if (this.#leafIsDirty()) e.preventDefault();
    });
    this.#apply(new URL(location.href));
    // An effect, so a title function that reads a signal (the project's loaded title) stays current.
    effect(() => {
      const match = this.match();
      const titles = match ? match.chain.map((r) => titleOf(r, match.params)).filter(Boolean) : [];
      document.title = ['Read2Me', ...titles].join(' · ');
    });
  }

  navigate(
    path: string,
    options: { replace?: boolean; query?: Record<string, string | null> } = {},
  ): void {
    const url = new URL(path, document.baseURI);
    for (const [key, value] of Object.entries(options.query ?? {})) {
      if (value === null) url.searchParams.delete(key);
      else url.searchParams.set(key, value);
    }
    navigation.navigate(url.href, { history: options.replace ? 'replace' : 'push' });
  }
  /** Whether the current leaf is guarded and its element holds unsaved edits. */
  #leafIsDirty(): boolean {
    const leaf = this.match()?.chain.at(-1);
    if (!leaf?.guardUnsaved || !leaf.tag) return false;
    const element = document.querySelector<Element & Partial<HasUnsavedChanges>>(leaf.tag);
    return element?.hasUnsavedChanges?.() === true;
  }

  #onNavigate(e: NavigateEvent): void {
    if (!e.canIntercept || e.hashChange || e.downloadRequest !== null || e.formData) return;
    const url = new URL(e.destination.url);
    // Outside the base (the other app, /workspace media) the browser navigates as usual.
    if (this.#relative(url) === null) return;

    const dirty = this.#leafIsDirty();
    // One-shot: it lets through the navigation the guard itself repeats, whatever becomes of it.
    const bypass = this.#bypassNext;
    this.#bypassNext = false;
    if (dirty && !bypass && url.pathname !== this.#url().pathname && e.cancelable) {
      e.preventDefault();
      void this.#confirmThenRepeat(e);
      return;
    }
    e.intercept({ handler: async () => this.#apply(url) });
  }

  async #confirmThenRepeat(e: NavigateEvent): Promise<void> {
    if (!(await this.#confirmLeave())) return;
    this.#bypassNext = true;
    if (e.navigationType === 'traverse') navigation.traverseTo(e.destination.key);
    else {
      navigation.navigate(e.destination.url, {
        history: e.navigationType === 'replace' ? 'replace' : 'push',
      });
    }
  }

  #apply(url: URL): void {
    const match = this.#resolve(url);
    const redirect = match?.chain.at(-1)?.redirectTo;
    if (redirect !== undefined) {
      this.navigate(fill(redirect, match?.params ?? {}), { replace: true });
      return;
    }
    this.#url.set(url);
  }

  /** `url`'s path below the base as `/…` without a trailing slash, or null when outside it. */
  #relative(url: URL): string | null {
    const path = url.pathname;
    if (path === this.#base.slice(0, -1)) return '/';
    if (!path.startsWith(this.#base)) return null;
    return `/${path.slice(this.#base.length).replace(/\/$/, '')}`;
  }

  #resolve(url: URL): MatchedRoute | null {
    const pathname = this.#relative(url);
    if (pathname === null) return null;
    for (const route of this.#routes) {
      const hit = route.pattern.exec({ pathname });
      if (hit) {
        const params = Object.fromEntries(
          Object.entries(hit.pathname.groups)
            .filter((e): e is [string, string] => e[1] !== undefined)
            .map(([k, v]) => [k, decodeURIComponent(v)]),
        );
        return { chain: route.chain, params };
      }
    }
    return null;
  }
}

export function titleOf(route: RouteDef, params: Record<string, string>): string {
  return typeof route.title === 'function' ? route.title(params) : (route.title ?? '');
}

/** Replaces `:name` segments with the encoded params: a route path or `redirectTo` made concrete. */
export function fill(path: string, params: Record<string, string>): string {
  return path.replace(/:(\w+)/g, (_, name: string) => encodeURIComponent(params[name] ?? ''));
}

function flatten(routes: RouteDef[], prefix: string, parents: RouteDef[]): FlatRoute[] {
  return routes.flatMap((route) => {
    const path = route.path ? `${prefix}/${route.path}` : prefix;
    const chain = [...parents, route];
    if (route.children) return flatten(route.children, path, chain);
    return [{ pattern: new URLPattern({ pathname: path || '/' }), chain }];
  });
}
