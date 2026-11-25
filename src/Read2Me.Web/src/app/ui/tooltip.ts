/**
 * The matTooltip replacement: any element with `data-tooltip` gets a hover/focus hint after a
 * delay. One shared `popover="hint"`-style element is positioned with CSS anchor positioning —
 * the hovered element becomes the anchor. `interestfor` would do this declaratively, but Firefox
 * lacks it (native-platform research), so the trigger is these few lines.
 *
 * The tip can be hovered (WCAG 1.4.13): leaving the trigger or the tip hides it after
 * `HIDE_DELAY_MS`, and entering either one cancels that, so the pointer can cross onto it. Focus
 * out (with the pointer off the tip) and Escape still hide it at once.
 *
 * Two opt-ins:
 * - `data-tooltip-overflow` on the element: show the tip only when, at show time, its content is
 *   wider than its box (`scrollWidth > clientWidth`), i.e. a one-line text cut off by an ellipsis.
 *   Measured once per hover or focus, with no observer.
 * - `data-tooltip-delegate="<selector>"` on a focusable element with no tip of its own: focusing
 *   it shows the tip of the descendant the selector names, anchored to that descendant (a tree row
 *   whose title carries the tip).
 */
const SHOW_DELAY_MS = 400;
/** Time to move the pointer from the trigger onto the tip before it closes. */
const HIDE_DELAY_MS = 200;

export function installTooltips(root: Document = document): void {
  const tip = root.createElement('div');
  tip.className = 'r2m-tooltip';
  tip.popover = 'manual';
  tip.setAttribute('role', 'tooltip');
  root.body.append(tip);

  let anchor: HTMLElement | null = null;
  let showTimer: ReturnType<typeof setTimeout> | undefined;
  let hideTimer: ReturnType<typeof setTimeout> | undefined;
  const isOpen = () => tip.matches(':popover-open');

  const cancelTimers = () => {
    clearTimeout(showTimer);
    clearTimeout(hideTimer);
  };

  const show = (target: HTMLElement) => {
    cancelTimers();
    showTimer = setTimeout(() => {
      if ('tooltipOverflow' in target.dataset && target.scrollWidth <= target.clientWidth) {
        hide();
        return;
      }
      anchor?.style.removeProperty('anchor-name');
      anchor = target;
      target.style.setProperty('anchor-name', '--r2m-tooltip');
      tip.textContent = target.dataset['tooltip'] ?? '';
      if (isOpen()) tip.hidePopover();
      tip.showPopover();
    }, SHOW_DELAY_MS);
  };
  const hide = () => {
    cancelTimers();
    anchor?.style.removeProperty('anchor-name');
    anchor = null;
    if (isOpen()) tip.hidePopover();
  };
  /** The pointer left the trigger and the tip: a pending show is dropped, an open tip lingers. */
  const hideSoon = () => {
    if (!isOpen()) return hide();
    cancelTimers();
    hideTimer = setTimeout(hide, HIDE_DELAY_MS);
  };
  const targetOf = (e: Event) =>
    (e.target instanceof Element ? e.target.closest<HTMLElement>('[data-tooltip]') : null) ?? null;
  const delegateOf = (e: Event) => {
    if (!(e.target instanceof HTMLElement)) return null;
    const selector = e.target.dataset['tooltipDelegate'];
    return selector ? e.target.querySelector<HTMLElement>(selector) : null;
  };

  root.addEventListener('pointerover', (e) => {
    if (e.target instanceof Node && tip.contains(e.target)) {
      if (isOpen()) cancelTimers(); // it stays, and a trigger it passed over doesn't take over
      return;
    }
    const target = targetOf(e);
    if (target && target === anchor) clearTimeout(hideTimer);
    else if (target) show(target);
    else hideSoon();
  });
  root.addEventListener('focusin', (e) => {
    const target = targetOf(e) ?? delegateOf(e);
    if (target) show(target);
  });
  // A click on the tip blurs its trigger; the pointer still on the tip keeps it.
  root.addEventListener('focusout', () => tip.matches(':hover') || hide());
  root.addEventListener('keydown', (e) => e.key === 'Escape' && hide());
}
