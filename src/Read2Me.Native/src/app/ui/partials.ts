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
    >${icon(options.icon ?? DEFAULT_ICONS[options.status])}<span>${options.label}</span></span
  >`;
}

export function emptyState(
  options: { icon: IconName; headline: string; hint?: string; compact?: boolean },
  action: unknown = nothing,
) {
  return html`<div class="r2m-empty-state ${options.compact ? 'r2m-empty-state--compact' : ''}">
    ${icon(options.icon, 'r2m-empty-state__icon')}
    <p class="r2m-empty-state__headline">${options.headline}</p>
    ${options.hint ? html`<p class="r2m-empty-state__hint">${options.hint}</p>` : nothing} ${action}
  </div>`;
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
