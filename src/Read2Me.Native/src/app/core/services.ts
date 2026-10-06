/**
 * The `inject()` replacement. `use(X)` returns the app-wide instance of a class (created on first
 * use with no arguments) or of a {@link token}; `use(X, element)` first looks for an instance an
 * ancestor element provided with {@link provide} — the page-scoped stores (`providers: [CastStore]`).
 * Tests swap instances with {@link override} and start clean with {@link resetServices}.
 */

export interface Token<T> {
  readonly name: string;
  readonly factory: () => T;
}

export type ServiceKey<T> = (new () => T) | Token<T>;

export function token<T>(name: string, factory: () => T): Token<T> {
  return { name, factory };
}

const root = new Map<ServiceKey<unknown>, unknown>();
const scopes = new WeakMap<Element, Map<ServiceKey<unknown>, unknown>>();

export function use<T>(key: ServiceKey<T>, from?: Element): T {
  for (let el: Element | null = from ?? null; el; el = parentOf(el)) {
    const scoped = scopes.get(el);
    if (scoped?.has(key)) return scoped.get(key) as T;
  }
  if (!root.has(key)) root.set(key, typeof key === 'function' ? new key() : key.factory());
  return root.get(key) as T;
}

/** Makes `instance` what descendants of `host` get from `use(key, this)`. */
export function provide<T>(host: Element, key: ServiceKey<T>, instance: T): T {
  let scoped = scopes.get(host);
  if (!scoped) scopes.set(host, (scoped = new Map()));
  scoped.set(key, instance);
  return instance;
}

export function override<T>(key: ServiceKey<T>, instance: T): void {
  root.set(key, instance);
}

export function resetServices(): void {
  root.clear();
}

function parentOf(el: Element): Element | null {
  return el.parentElement ?? ((el.getRootNode() as ShadowRoot).host || null);
}
