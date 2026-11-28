/** The visible height {@link fakeScroller} gives the scroller. */
const CLIENT_HEIGHT = 200;

/**
 * Layout for a scroll container in a spec, since happy-dom has none: set `height` to the content's
 * height, and `scrollTop` is clamped to it as a browser's is. `userScrollsTo` moves the view and
 * fires the `scroll` event; a write by the code under test fires none, as in happy-dom.
 */
export function fakeScroller(el: HTMLElement) {
  let top = 0;
  const view = {
    el,
    height: 0,
    userScrollsTo(y: number): void {
      el.scrollTop = y;
      el.dispatchEvent(new Event('scroll'));
    },
  };
  const bottom = () => Math.max(0, view.height - CLIENT_HEIGHT);
  Object.defineProperties(el, {
    scrollHeight: { get: () => view.height, configurable: true },
    clientHeight: { get: () => CLIENT_HEIGHT, configurable: true },
    scrollTop: {
      get: () => Math.min(top, bottom()),
      set: (y: number) => {
        top = Math.min(Math.max(0, y), bottom());
      },
      configurable: true,
    },
  });
  return view;
}
