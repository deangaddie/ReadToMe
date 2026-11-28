/**
 * A controllable `IntersectionObserver` for a spec, since happy-dom's never fires. `install()`
 * swaps it in and returns `restore`; `enter(el)` reports `el` intersecting to every live observer
 * watching it; `watched(el)` says whether one still is; `options` holds the last observer's init.
 */
export function fakeIntersection() {
  const live = new Set<Fake>();
  let options: IntersectionObserverInit | undefined;

  class Fake {
    readonly targets = new Set<Element>();
    constructor(
      readonly callback: IntersectionObserverCallback,
      init?: IntersectionObserverInit,
    ) {
      options = init;
      live.add(this);
    }
    observe(el: Element): void {
      this.targets.add(el);
    }
    unobserve(el: Element): void {
      this.targets.delete(el);
    }
    disconnect(): void {
      this.targets.clear();
      live.delete(this);
    }
    takeRecords(): IntersectionObserverEntry[] {
      return [];
    }
  }

  const original = globalThis.IntersectionObserver;
  return {
    get options() {
      return options;
    },
    install(): () => void {
      globalThis.IntersectionObserver = Fake as unknown as typeof IntersectionObserver;
      return () => {
        globalThis.IntersectionObserver = original;
        live.clear();
      };
    },
    enter(el: Element, isIntersecting = true): void {
      for (const observer of live) {
        if (!observer.targets.has(el)) continue;
        const entry = { target: el, isIntersecting } as unknown as IntersectionObserverEntry;
        observer.callback([entry], observer as unknown as IntersectionObserver);
      }
    },
    watched(el: Element): boolean {
      return [...live].some((observer) => observer.targets.has(el));
    },
  };
}
