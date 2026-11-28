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
 *   Measured once per hover or focus, with no observer. A label that fits hands over to the
 *   nearest ancestor with a tip (a button whose own tip gives advice).
 * - `data-tooltip-delegate="<selector>"` on an element: focusing it, or a control inside it with
 *   no tip of its own, shows the tip of the descendant the selector names, anchored to that
 *   descendant (a tree row whose title carries the tip, the Download link of an export row). On an
 *   element with a tip of its own, the descendant goes first and, fitting, hands back to it.
 *
 * The shared hint is the visual cue only: no trigger points at it, since it holds a trigger's text
 * only while open. A tip that says more than the trigger's name goes through `withTip`, which
 * renders it a second time as the trigger's accessible description.
 */
import { type TemplateResult, html, nothing } from 'lit-html';
import { Directive, directive } from 'lit-html/directive.js';

/** What a trigger binds for its tip; see `withTip`. */
export interface Tip {
  /** The trigger's `data-tooltip`. */
  tooltip: string | typeof nothing;
  /** The `aria-describedby` of the control the tip belongs to. */
  describedBy: string | typeof nothing;
  /** The element that holds the description: place it inside a positioned box. */
  description: TemplateResult | typeof nothing;
}

let nextId = 0;

/** One per rendered trigger, so its description keeps one id for as long as the trigger lives. */
class TipDirective extends Directive {
  readonly #id = `r2m-tip-${nextId++}`;

  render(text: string | undefined, name: string | undefined, body: (tip: Tip) => unknown) {
    const describes = !!text && text !== name;
    return body({
      tooltip: text || nothing,
      describedBy: describes ? this.#id : nothing,
      description: describes
        ? html`<span class="visually-hidden" id=${this.#id}>${text}</span>`
        : nothing,
    });
  }
}

/**
 * Renders a trigger with its tip: `body` is handed the `data-tooltip` value and, when `text` says
 * more than `name` (the trigger's accessible name), a visually hidden description with the
 * `aria-describedby` that points at it. A tip that equals the name adds no description, and an
 * empty one adds nothing.
 */
export const withTip = directive(TipDirective);

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

  /** The element whose tip shows: a fitting overflow label hands over to the nearest tipped ancestor. */
  const resolve = (el: HTMLElement | null): HTMLElement | null => {
    while (el && 'tooltipOverflow' in el.dataset && el.scrollWidth <= el.clientWidth)
      el = el.parentElement?.closest<HTMLElement>('[data-tooltip]') ?? null;
    return el;
  };
  const show = (trigger: HTMLElement) => {
    cancelTimers();
    showTimer = setTimeout(() => {
      const target = resolve(trigger);
      if (!target) return hide();
      if (target === anchor && isOpen()) return; // the pointer moved between a label and its trigger
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
  /** On focus: the delegated descendant, unless the focused control inside the host has its own tip. */
  const focusTargetOf = (e: Event) => {
    const own = targetOf(e);
    if (!(e.target instanceof Element)) return own;
    const host = e.target.closest<HTMLElement>('[data-tooltip-delegate]');
    const delegated = host?.querySelector<HTMLElement>(host.dataset['tooltipDelegate'] ?? '');
    return delegated && (!own || own.contains(host!)) ? delegated : own;
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
    const target = focusTargetOf(e);
    if (target) show(target);
  });
  // A click on the tip blurs its trigger; the pointer still on the tip keeps it.
  root.addEventListener('focusout', () => tip.matches(':hover') || hide());
  root.addEventListener('keydown', (e) => e.key === 'Escape' && hide());
}
