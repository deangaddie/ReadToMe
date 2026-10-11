import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { type AttributionChainStep, AttributionPromptStyle, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { closeMenuOf } from '@app/ui/menu';
import { icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  type ChainOption,
  type ChainRow,
  addStep,
  moveStep,
  optionLabel,
  removeStep,
} from './chain-steps';
import { LlmSettingsStore } from './llm-settings-store';

const ADD_MENU_ID = 'r2m-llm-chain-add-menu';

/**
 * The attribution escalation chain (ticket 21): ordered rungs with Move up / down and Remove,
 * Add step over config × thinking × style, and the self-consistency switch. Every change is one
 * PUT of the whole chain. Reads the page's {@link LlmSettingsStore}.
 */
export class LlmChainCard extends R2mElement {
  private readonly toast = use(ToastService);
  private store!: LlmSettingsStore;

  readonly busy = signal(false);

  protected override connected(): void {
    this.classList.add('chain');
    this.store = use(LlmSettingsStore, this);
  }

  protected template() {
    const store = this.store;
    const busy = this.busy();
    // Read here, not inside the repeat callback: a lazy directive runs outside the element's effect.
    const rows = store.chainRows();
    return html`
      <h2 class="chain__title">Attribution chain</h2>
      <p class="chain__intro">
        Character attribution runs each step in order. The first answers the easy lines; suspect
        answers (unknown, unlisted name, parse failure) escalate to the next. A config can be added
        once per variant (prompt style and thinking), fixed when the step is added.
        <b>Thinking</b> runs the step with model thinking on: slower, higher recall.
        <b>Simple</b> asks with the strict prompt, which names a speaker only when the text does and
        otherwise answers "unknown" — far less confabulation, more escalation. <b>Chapter</b> walks
        each chapter one line at a time with roster-only answers (fast models only; thinking is
        always off) and sends unknowns on to the next step. To change a step's mode, remove it and
        add the other variant.
      </p>

      ${
        !store.chainLoaded()
          ? html`<p class="chain__muted">Loading…</p>`
          : html`
              ${this.emptyTemplate()}
              <ol class="chain__steps">
                ${repeat(
                  rows,
                  (row) => row.index,
                  (row, i) => this.stepTemplate(row, i === 0, i === rows.length - 1, busy),
                )}
              </ol>
              ${this.addTemplate(store.chainOptions(), busy)}
              <label class="r2m-switch chain__self">
                <input
                  type="checkbox"
                  role="switch"
                  .checked=${store.selfConsistency()}
                  ?disabled=${busy}
                  @change=${(e: Event) =>
                    void this.setSelfConsistency((e.target as HTMLInputElement).checked)}
                />
                Self-consistency: sample twice on non-final steps and escalate on disagreement —
                doubles LLM calls for those steps
              </label>
            `
      }
    `;
  }

  private emptyTemplate() {
    if (this.store.chainRows().length !== 0) return nothing;
    const fallback = this.store.chainFallback();
    return fallback
      ? html`<p class="chain__alert chain__alert--info" role="status">
          The chain is empty — attribution falls back to the default config
          <b>${fallback.name}</b>. Add steps below to run a multi-step chain.
        </p>`
      : html`<p class="chain__alert chain__alert--warn" role="alert">
          The chain is empty and no default config is selected — attribution has no model to run.
        </p>`;
  }

  private stepTemplate(row: ChainRow, first: boolean, last: boolean, busy: boolean) {
    return html`<li class="chain__step">
      <button
        type="button"
        class="r2m-icon-button"
        ?disabled=${first || busy}
        aria-label=${`Move ${row.config.name} up`}
        @click=${() => void this.move(row.index, -1)}
      >
        ${icon('arrow_upward')}
      </button>
      <button
        type="button"
        class="r2m-icon-button"
        ?disabled=${last || busy}
        aria-label=${`Move ${row.config.name} down`}
        @click=${() => void this.move(row.index, 1)}
      >
        ${icon('arrow_downward')}
      </button>
      <span class="chain__name">${row.config.name}</span>
      ${row.config.model ? html`<span class="chain__model">${row.config.model}</span>` : nothing}
      <span class="chain__flags">
        ${
          row.style === AttributionPromptStyle.Simple
            ? statusChip({ status: 'info', label: 'Simple', compact: true })
            : nothing
        }
        ${
          row.style === AttributionPromptStyle.Chapter
            ? statusChip({ status: 'info', label: 'Chapter', compact: true })
            : nothing
        }
        ${row.thinking ? statusChip({ status: 'info', label: 'Thinking', compact: true }) : nothing}
      </span>
      <button
        type="button"
        class="r2m-icon-button chain__remove"
        ?disabled=${busy}
        aria-label=${`Remove ${row.config.name}`}
        data-tooltip="Remove"
        @click=${() => void this.removeRung(row.index)}
      >
        ${icon('delete')}
      </button>
    </li>`;
  }

  private addTemplate(options: ChainOption[], busy: boolean) {
    if (!options.length) return nothing;
    return html`
      <button
        type="button"
        class="r2m-button r2m-button--stroked"
        data-action="add-step"
        popovertarget=${ADD_MENU_ID}
        aria-haspopup="menu"
        ?disabled=${busy}
      >
        ${icon('add')} Add step
      </button>
      <div id=${ADD_MENU_ID} popover class="r2m-menu" role="menu" aria-label="Add step">
        ${repeat(
          options,
          optionLabel,
          (option) =>
            html`<button
              type="button"
              class="r2m-menu__item"
              role="menuitem"
              @click=${(e: Event) => {
                closeMenuOf(e);
                void this.add(option);
              }}
            >
              ${optionLabel(option)}
            </button>`,
        )}
      </div>
    `;
  }

  private move(index: number, delta: -1 | 1): Promise<void> {
    return this.save(moveStep(this.store.steps(), index, delta));
  }

  /** Not `remove`: that name is `HTMLElement.remove()`. */
  private removeRung(index: number): Promise<void> {
    return this.save(removeStep(this.store.steps(), index));
  }

  private add(option: ChainOption): Promise<void> {
    const { config, thinking, promptStyle } = option;
    return this.save(addStep(this.store.steps(), { configId: config.id, thinking, promptStyle }));
  }

  private setSelfConsistency(value: boolean): Promise<void> {
    return this.save(this.store.steps(), value);
  }

  private async save(
    steps: AttributionChainStep[],
    selfConsistency = this.store.selfConsistency(),
  ): Promise<void> {
    if (steps === this.store.steps() && selfConsistency === this.store.selfConsistency()) return;
    this.busy.set(true);
    try {
      await this.store.saveChain(steps, selfConsistency);
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.busy.set(false);
    }
  }
}
define('r2m-llm-chain-card', LlmChainCard);
