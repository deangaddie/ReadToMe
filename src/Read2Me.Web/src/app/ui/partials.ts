import { html, nothing } from 'lit-html';
import type { IconName } from './icons';

/**
 * Presentational partials: stateless markup is a function returning a template, not an element.
 * Elements are kept for things with their own state or lifecycle.
 */

export type StatusKind = 'ok' | 'warn' | 'error' | 'info' | 'busy' | 'neutral';

const DEFAULT_ICONS: Record<StatusKind, IconName> = {
  ok: 'check_circle',
  warn: 'warning',
  error: 'error',
  info: 'info',
  busy: 'progress_activity',
  neutral: 'radio_button_unchecked',
};

/** Material Symbols ligature icon, drawn from the subset font that holds only `IconName`. */
export const icon = (name: IconName, cls = '') =>
  html`<span class="material-symbols-rounded r2m-icon ${cls}" aria-hidden="true">${name}</span>`;

/** Status conveyed by colour *and* icon *and* text, never colour alone (design §7). */
export function statusChip(options: {
  status: StatusKind;
  label: string;
  icon?: IconName;
  tooltip?: string;
  compact?: boolean;
}) {
  return html`<span
    class="r2m-status-chip r2m-status-chip--${options.status} ${options.compact ? 'r2m-status-chip--compact' : ''}"
    role="status"
    data-tooltip=${options.tooltip ?? nothing}
    >${icon(options.icon ?? DEFAULT_ICONS[options.status])}<span class="r2m-status-chip__label"
      >${options.label}</span
    ></span
  >`;
}

/**
 * One action a message offers: an in-place command renders as a button, something that lives
 * elsewhere as a link. The label is the real control's exact button label.
 */
export type MessageAction =
  | { label: string; icon?: IconName; run: () => void }
  | { label: string; icon?: IconName; href: string };

export type EmptyStateAction = MessageAction;

function messageAction(action: MessageAction, cls: string) {
  const content = html`${action.icon ? icon(action.icon) : nothing}${action.label}`;
  return 'href' in action
    ? html`<a class="r2m-button ${cls}" href=${action.href}>${content}</a>`
    : html`<button type="button" class="r2m-button ${cls}" @click=${action.run}>
        ${content}
      </button>`;
}

/** An empty view: what is missing, why, and at most two actions (the first filled). */
export function emptyState(
  options: { icon: IconName; headline: string; hint?: string; compact?: boolean },
  actions: readonly EmptyStateAction[] = [],
) {
  const shown = actions.slice(0, 2);
  return html`<div class="r2m-empty-state ${options.compact ? 'r2m-empty-state--compact' : ''}">
    ${icon(options.icon, 'r2m-empty-state__icon')}
    <p class="r2m-empty-state__headline">${options.headline}</p>
    ${options.hint ? html`<p class="r2m-empty-state__hint">${options.hint}</p>` : nothing}
    ${
      shown.length > 0
        ? html`<div class="r2m-empty-state__actions">
            ${shown.map((a, i) =>
              messageAction(a, `r2m-empty-state__action ${i === 0 ? 'r2m-button--filled' : ''}`),
            )}
          </div>`
        : nothing
    }
  </div>`;
}

/**
 * Makes a disabled control's blocked reason reachable: a `data-tooltip` wrapper that takes focus
 * only while blocked, since a disabled button cannot (`ui/tooltip.ts` shows on `focusin`, so a tap
 * shows it too). The control still sets `?disabled=${reason !== null}` itself; the reason comes
 * from a pure `xBlockedReason` in the feature folder. A momentary busy carries no reason. A control
 * kept out of the tab order (a tree node's trigger, reached by the tree's own keys) passes
 * `tabbable: false` so its wrapper stays out too.
 */
export function blockable(
  reason: string | null,
  control: unknown,
  { tabbable = true }: { tabbable?: boolean } = {},
) {
  return html`<span
    class="r2m-blockable"
    data-tooltip=${reason ?? nothing}
    tabindex=${reason === null || !tabbable ? nothing : '0'}
    >${control}</span
  >`;
}

export interface Callout {
  kind: 'info' | 'warn';
  text: string;
  action?: MessageAction;
}

/**
 * A state-driven banner with no dismiss: it clears when its condition does. Shown under an
 * Overview step row and at the top of the step's page, both from one pure `xCallout` in the stage's
 * feature folder. Errors stay toasts.
 */
export function callout(options: Callout) {
  return html`<div class="r2m-callout r2m-callout--${options.kind}">
    ${icon(DEFAULT_ICONS[options.kind], 'r2m-callout__icon')}
    <span class="r2m-callout__text">${options.text}</span>
    ${options.action ? messageAction(options.action, 'r2m-callout__action') : nothing}
  </div>`;
}

/** The one muted sentence an out-of-order action shows before it is clicked, or nothing. */
export function adviceLine(text: string | null) {
  return text === null ? nothing : html`<p class="r2m-advice">${text}</p>`;
}

export type CountBadgeKind = 'attribution' | 'audio' | 'review';

const BADGE_ICONS: Record<CountBadgeKind, IconName> = {
  attribution: 'person_search',
  audio: 'graphic_eq',
  review: 'rate_review',
};

/** Indeterminate progress spinner (mat-progress-spinner): `size` in px, colour from `currentColor`. */
export function spinner(size = 20, cls = '') {
  return html`<span
    class="r2m-spinner ${cls}"
    role="progressbar"
    aria-label="Working"
    style="--_size: ${size}px"
  ></span>`;
}

/** Count badge (design §7): number + icon coloured by kind; nothing at zero unless `showZero`. */
export function countBadge(kind: CountBadgeKind, count: number, tooltip: string, showZero = false) {
  if (count === 0 && !showZero) return nothing;
  return html`<span class="r2m-count-badge r2m-count-badge--${kind}" data-tooltip=${tooltip}
    >${icon(BADGE_ICONS[kind], 'r2m-count-badge__icon')}<span class="r2m-count-badge__count"
      >${count}</span
    ></span
  >`;
}
