import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { ActivityStore } from '@app/activity/activity-store';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { adviceLine, emptyState, icon, spinner } from '@app/ui/partials';
import { type HistoryRow, historyGroups } from './history-rows';
import { HistorySheetState } from './history-sheet-state';
import type { HistoryStore } from './history-store';
import sheetCss from './history-sheet.css' with { type: 'text' };

adoptStyles(sheetCss);

export const HISTORY_ADVICE =
  'Click a step to see the whole project as it was after it. Restoring adds a new step; nothing is lost.';

/**
 * The History sheet (spec, "The History sheet"; prototype variant A): the project's steps newest
 * first, grouped by day, each with a kind icon, its label and note, what changed and when, the tip
 * marked "Current". The app shell docks it beside the page, or over the page body under 900 px.
 * It reads the list once when it opens; the store keeps it live from the hub. Clicking a row views
 * its step, and the tip returns to the current project; while the project is busy no step can be
 * viewed, and the sheet says why and opens the activity drawer.
 */
export class HistorySheet extends R2mElement {
  private readonly state = use(HistorySheetState);
  private readonly activity = use(ActivityStore);
  readonly #store = signal<HistoryStore | null>(null);

  get store(): HistoryStore | null {
    return this.#store();
  }
  set store(value: HistoryStore | null) {
    this.#store.set(value);
  }

  protected override connected(): void {
    this.classList.add('history-sheet');
    this.effect(() => {
      const store = this.#store();
      if (store) untracked(() => void store.load());
    });
  }

  protected template() {
    const store = this.#store();
    return html`
      <header class="history-sheet__head">
        <h2 class="history-sheet__title" id="history-sheet-title">History</h2>
        <button
          type="button"
          class="r2m-icon-button"
          aria-label="Close History"
          data-tooltip="Close History"
          @click=${() => this.state.close()}
        >
          ${icon('close')}
        </button>
      </header>
      ${adviceLine(HISTORY_ADVICE)} ${store ? this.body(store) : nothing}
    `;
  }

  private body(store: HistoryStore) {
    const steps = store.steps();
    const error = store.error();
    if (!store.loaded()) {
      return error
        ? html`<p class="history-sheet__error" role="alert">${error}</p>`
        : html`<div class="history-sheet__loading">${spinner(24)}</div>`;
    }
    if (steps.length === 0)
      return emptyState({
        icon: 'history',
        headline: 'No steps yet',
        hint: 'Every change you make to this project is recorded here.',
        compact: true,
      });
    // Read here, not in the row template: `repeat` runs that outside the render effect.
    const groups = historyGroups(steps, store.headStepId(), new Date());
    const busy = store.busy();
    const rows: RowContext = {
      viewing: store.viewing(),
      busy: busy !== null,
      view: (stepId) => store.view(stepId),
    };
    return html`${busy === null ? nothing : this.busyNotice(busy)}
    ${groups.map(
      (group) =>
        html`<section class="history-sheet__day" aria-label=${group.day}>
          <h3 class="history-sheet__day-title">${group.day}</h3>
          <ol class="history-sheet__list">
            ${repeat(
              group.rows,
              (row) => row.stepId,
              (row) => historyRowView(row, rows),
            )}
          </ol>
        </section>`,
    )}`;
  }

  /** Steps can't be viewed while anything is queued or running for the project; the drawer shows what. */
  private busyNotice(reason: string) {
    return html`<div class="history-sheet__busy" role="status">
      ${icon('hourglass_empty')}
      <span class="history-sheet__busy-text"
        >Steps can't be viewed while the project is busy: ${reason}.</span
      >
      <button type="button" class="r2m-button" @click=${() => this.activity.openDrawer()}>
        Show activity
      </button>
    </div>`;
  }
}
define('r2m-history-sheet', HistorySheet);

interface RowContext {
  /** The viewed step, or null on the current project. */
  viewing: string | null;
  busy: boolean;
  view: (stepId: string) => void;
}

/** A row is a button that views its step; the tip returns to the current project. */
function historyRowView(row: HistoryRow, { viewing, busy, view }: RowContext) {
  const viewed = row.stepId === viewing;
  return html`<li
    class="history-sheet__row history-sheet__row--${row.kind} ${row.current ? 'history-sheet__row--current' : ''} ${viewed ? 'history-sheet__row--viewing' : ''}"
    data-step-id=${row.stepId}
    aria-current=${row.current ? 'step' : nothing}
  >
    <button
      type="button"
      class="history-sheet__step"
      aria-pressed=${viewed || (row.current && viewing === null)}
      ?disabled=${busy || row.cantOpen}
      @click=${() => view(row.stepId)}
    >
      <span class="history-sheet__icon">${icon(row.icon)}</span>
      <span class="history-sheet__text">
        <span class="history-sheet__label"
          >${row.label}${row.note ? html` <em class="history-sheet__note">${row.note}</em>` : nothing}</span
        >
        ${row.summary ? html`<span class="history-sheet__summary">${row.summary}</span>` : nothing}
        ${row.cantOpen ? html`<span class="history-sheet__cant-open">can't be opened</span>` : nothing}
      </span>
      <span class="history-sheet__when">
        ${row.current ? html`<span class="history-sheet__current">Current</span>` : nothing}
        <span class="history-sheet__time">${row.time}</span>
      </span>
    </button>
  </li>`;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-history-sheet': HistorySheet;
  }
}
