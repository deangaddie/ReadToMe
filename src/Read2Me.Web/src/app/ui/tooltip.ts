/**
 * The matTooltip replacement: any element with `data-tooltip` gets a hover/focus hint after a
 * delay. One shared `popover="hint"`-style element is positioned with CSS anchor positioning —
 * the hovered element becomes the anchor. `interestfor` would do this declaratively, but Firefox
 * lacks it (native-platform research), so the trigger is these few lines.
 */
const SHOW_DELAY_MS = 400;

export function installTooltips(root: Document = document): void {
  const tip = root.createElement('div');
  tip.className = 'r2m-tooltip';
  tip.popover = 'manual';
  tip.setAttribute('role', 'tooltip');
  root.body.append(tip);

  let anchor: HTMLElement | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const show = (target: HTMLElement) => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      anchor?.style.removeProperty('anchor-name');
      anchor = target;
      target.style.setProperty('anchor-name', '--r2m-tooltip');
      tip.textContent = target.dataset['tooltip'] ?? '';
      if (tip.matches(':popover-open')) tip.hidePopover();
      tip.showPopover();
    }, SHOW_DELAY_MS);
  };
  const hide = () => {
    clearTimeout(timer);
    anchor?.style.removeProperty('anchor-name');
    anchor = null;
    if (tip.matches(':popover-open')) tip.hidePopover();
  };
  const targetOf = (e: Event) =>
    (e.target instanceof Element ? e.target.closest<HTMLElement>('[data-tooltip]') : null) ?? null;

  root.addEventListener('pointerover', (e) => {
    const target = targetOf(e);
    if (target && target !== anchor) show(target);
    else if (!target) hide();
  });
  root.addEventListener('focusin', (e) => {
    const target = targetOf(e);
    if (target) show(target);
  });
  root.addEventListener('focusout', hide);
  root.addEventListener('keydown', (e) => e.key === 'Escape' && hide());
}
