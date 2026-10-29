import { html, nothing } from 'lit-html';
import type { SetupCheckRow } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { adoptStyles } from '@app/core/styles';
import { SetupCheckStore } from '@app/setup-check/setup-check-store';
import { callout, emptyState, icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { fixHint } from './fix-hint';
import { RESULT_VIEW, groupedRows, showsFix, summaryLine } from './setup-check-logic';
import css from './setup-check-page.css' with { type: 'text' };

adoptStyles(css);

/** The callout above the table in no-Workspace mode, where this page stands in for every route. */
const NO_WORKSPACE =
  'The app started without a usable Workspace, so nothing else is available. Fix the Workspace row below, then restart the app.';

/**
 * `/settings/setup-check`: one report on whether the install is sound. Opening the page shows the
 * host's last report and runs a fresh Quick check; Run Quick check runs another. One table, grouped
 * by area, with what each check expected, what it found and where to fix it.
 */
export class SetupCheckPage extends R2mElement {
  readonly store = use(SetupCheckStore);
  private readonly toast = use(ToastService);

  protected override connected(): void {
    this.classList.add('setup-check');
    void this.store.open();
  }

  protected template() {
    const report = this.store.report();
    const error = this.store.error();
    const checking = this.store.checking();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Setup check</h1>
        <p class="r2m-page-header__subtitle">
          What the app depends on, what it found, and where to fix it.
        </p>
        <span class="r2m-page-header__actions">
          <button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="run-quick"
            ?disabled=${checking}
            @click=${() => void this.store.runQuick()}
          >
            ${icon('refresh')} Run Quick check
          </button>
        </span>
      </header>
      ${
        report
          ? html`<p class="setup-check__summary" data-role="summary">${summaryLine(report)}</p>
              ${report.noWorkspace ? callout({ kind: 'warn', text: NO_WORKSPACE }) : nothing}
              ${this.table(report.rows)}`
          : error
            ? emptyState({ icon: 'cloud_off', headline: 'Setup check unavailable', hint: error })
            : html`<p class="setup-check__summary">Checking…</p>`
      }
    `;
  }

  private table(rows: SetupCheckRow[]) {
    return html`<table class="setup-check__table">
      <thead>
        <tr>
          <th scope="col">Check</th>
          <th scope="col">Result</th>
          <th scope="col">Expected</th>
          <th scope="col">Found</th>
          <th scope="col">Fix</th>
        </tr>
      </thead>
      ${groupedRows(rows).map(
        (g) =>
          html`<tbody>
            <tr class="setup-check__group">
              <th colspan="5" scope="colgroup">${g.label}</th>
            </tr>
            ${g.rows.map((row) => this.row(row))}
          </tbody>`,
      )}
    </table>`;
  }

  private row(row: SetupCheckRow) {
    return html`<tr
      class="setup-check__row ${row.result === 'failed' ? 'setup-check__row--failed' : ''}"
      data-check=${row.id}
      data-result=${row.result}
    >
      <th scope="row" class="setup-check__name">${row.name}</th>
      <td>${statusChip({ ...RESULT_VIEW[row.result], compact: true })}</td>
      <td class="setup-check__muted">${row.expected}</td>
      <td>
        ${row.found}
        ${
          row.note
            ? html`<div class="setup-check__note" data-role="note">
                ${icon('info')} <span>${row.note}</span>
              </div>`
            : nothing
        }
      </td>
      <td>
        ${
          showsFix(row)
            ? fixHint(row.fix, (text) => void this.copy(text))
            : html`<span class="setup-check__muted">—</span>`
        }
      </td>
    </tr>`;
  }

  private async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.toast.success('Copied');
    } catch {
      this.toast.warn("Couldn't copy: select the text and copy it by hand");
    }
  }
}
define('r2m-setup-check-page', SetupCheckPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-setup-check-page': SetupCheckPage;
  }
}
