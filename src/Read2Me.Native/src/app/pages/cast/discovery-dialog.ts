import { html, nothing } from 'lit-html';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { DiscoveryApi, toApiError } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { icon, statusChip } from '@app/ui/partials';
import { type AliasOwner, collides } from './alias-collisions';
import {
  type DiscoveryRow,
  applyRows,
  discoveryErrorMessage,
  discoveryFailureMessage,
  rowCollisions,
  toDiscoveryRows,
  withAlias,
  withoutAlias,
} from './discovery-rows';
import '@app/ui/stream-llm';

export interface DiscoveryDialogData {
  folder: string;
  /** The roster as it stands, for "Already exists" folding and collision detection. */
  roster: readonly AliasOwner[];
}

export interface DiscoveryDialogResult {
  applied: number;
}

export type DiscoveryPhase = 'discovering' | 'review' | 'failed';

/**
 * Discover characters (research §4 "Discover dialog"): Discovering (the LLM stream inline, Cancel)
 * → Review (editable rows: include, name, alias chips, "Already exists", collision warning) →
 * Add N selected. Re-run with the thinking toggle is the retry when the roster comes back thin;
 * Failed keeps the dialog open for exactly that. The stream group is joined for the dialog's
 * lifetime (design §9). Cancel while discovering closes the dialog; the host finishes the call
 * on its own and its answer is dropped.
 */
export class DiscoveryDialog extends R2mElement {
  private readonly feed = use(LlmStreamFeed);
  private readonly discovery = use(DiscoveryApi);

  data!: DiscoveryDialogData;

  readonly phase = signal<DiscoveryPhase>('discovering');
  readonly thinking = signal(false);
  readonly showStream = signal(false);
  readonly error = signal<string | null>(null);
  readonly rows = signal<DiscoveryRow[]>([]);
  readonly addingAliasFor = signal<number | null>(null);
  readonly applying = signal(false);
  /** The alias input was just opened: focus it after that render. */
  #focusAliasPending = false;

  readonly includedCount = computed(() => this.rows().filter((r) => r.included).length);
  readonly collisions = computed(() => rowCollisions(this.rows(), this.data.roster));
  readonly collisionList = computed(() =>
    [...this.collisions()].sort((a, b) => a.localeCompare(b)).join(', '),
  );

  /** Which discovery call is current; an answer from an earlier one is dropped. */
  private run = 0;

  protected override connected(): void {
    this.classList.add('discovery-dialog');
    this.feed.acquire();
    this.onDisconnect(() => {
      this.feed.release();
      this.run++;
    });
    void this.discover();
  }

  protected override updated(): void {
    if (!this.#focusAliasPending) return;
    this.#focusAliasPending = false;
    this.querySelector<HTMLInputElement>('.discover__input--alias')?.focus();
  }

  protected template() {
    const phase = this.phase();
    const error = this.error();
    const showStream = this.showStream();
    const discovering = phase === 'discovering';
    return html`
      <h2 class="r2m-dialog__title">Discover characters</h2>
      <div class="r2m-dialog__content discover" data-phase=${phase}>
        ${error ? html`<p class="discover__error" role="alert">${error}</p>` : nothing}

        <div class="discover__controls">
          <label class="r2m-switch">
            <input
              type="checkbox"
              role="switch"
              .checked=${this.thinking()}
              ?disabled=${discovering}
              @change=${(e: Event) => this.thinking.set((e.target as HTMLInputElement).checked)}
            />
            Enable thinking
          </label>
          <span class="discover__hint">Slower, better recall</span>
          <span class="discover__spacer"></span>
          <button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="rerun"
            ?disabled=${discovering}
            @click=${() => void this.discover()}
          >
            ${icon('refresh')} Re-run
          </button>
        </div>

        ${
          discovering
            ? html`<progress max="1" aria-label="Discovering"></progress>
                <p class="discover__hint">Asking the LLM for the book's characters…</p>`
            : phase === 'review'
              ? this.review()
              : nothing
        }

        <div class="discover__stream">
          <button
            type="button"
            class="r2m-button"
            aria-expanded=${showStream ? 'true' : 'false'}
            @click=${() => this.showStream.set(!showStream)}
          >
            ${icon(showStream ? 'expand_more' : 'expand_less')}
            ${showStream ? 'Hide AI activity' : 'Show AI activity'}
          </button>
          <div class="discover__stream-body ${showStream ? 'discover__stream-body--open' : ''}">
            <r2m-stream-llm
              .events=${this.feed.events()}
              .maxTurns=${this.feed.maxUnits}
            ></r2m-stream-llm>
          </div>
        </div>
      </div>
      <div class="r2m-dialog__actions">
        ${
          discovering
            ? html`<button
                type="button"
                class="r2m-button discover__cancel"
                @click=${() => this.emit('r2m-close', null)}
              >
                Cancel
              </button>`
            : html`<button
                  type="button"
                  class="r2m-button"
                  @click=${() => this.emit('r2m-close', null)}
                >
                  Close
                </button>
                <button
                  type="button"
                  class="r2m-button r2m-button--filled"
                  data-action="apply"
                  ?disabled=${this.includedCount() === 0 || this.applying()}
                  @click=${() => void this.apply()}
                >
                  Add ${this.includedCount()} selected
                </button>`
        }
      </div>
    `;
  }

  private review() {
    const rows = this.rows();
    const collisions = this.collisions();
    const adding = this.addingAliasFor();
    return html`
      ${
        collisions.length > 0
          ? html`<p class="discover__warning" role="alert" data-testid="collision-warning">
              Shared by more than one character: ${this.collisionList()}. Attribution resolves a
              name to a single character, so a shared one always picks the same character and never
              the others. Remove it from all but one row.
            </p>`
          : nothing
      }
      <div class="discover__select">
        <button type="button" class="r2m-button" @click=${() => this.setAll(true)}>
          Select all
        </button>
        <button type="button" class="r2m-button" @click=${() => this.setAll(false)}>
          Select none
        </button>
        <span class="discover__spacer"></span>
        <span class="discover__hint">${this.includedCount()} of ${rows.length} selected</span>
      </div>
      <div class="discover__rows">
        ${rows.length === 0 ? html`<p class="discover__hint">No characters found.</p>` : nothing}
        ${rows.map(
          (row, i) =>
            html`<div class="discover__row" data-row=${i}>
              <input
                type="checkbox"
                class="r2m-checkbox discover__include"
                aria-label=${`Include ${row.name}`}
                .checked=${row.included}
                @change=${(e: Event) => this.patch(i, { included: (e.target as HTMLInputElement).checked })}
              />
              <div class="discover__body">
                <div class="discover__name">
                  <input
                    class="discover__input"
                    type="text"
                    aria-label="Character name"
                    .value=${row.name}
                    aria-invalid=${collides(collisions, row.name) ? 'true' : 'false'}
                    @input=${(e: Event) => this.patch(i, { name: (e.target as HTMLInputElement).value })}
                  />
                  ${
                    row.existingCharacterId
                      ? statusChip({ status: 'info', label: 'Already exists', compact: true })
                      : nothing
                  }
                </div>
                <div class="discover__aliases">
                  ${row.aliases.map(
                    (alias) =>
                      html`<span
                        class="discover__alias ${collides(collisions, alias) ? 'discover__alias--collides' : ''}"
                      >
                        ${alias}
                        <button
                          type="button"
                          class="discover__alias-remove"
                          aria-label=${`Remove alias ${alias}`}
                          @click=${() => this.removeAlias(i, alias)}
                        >
                          ${icon('close')}
                        </button>
                      </span>`,
                  )}
                  ${
                    adding === i
                      ? html`<input
                          class="discover__input discover__input--alias"
                          type="text"
                          placeholder="Alias name"
                          aria-label="New alias"
                          @keydown=${(e: KeyboardEvent) => this.onAliasKeydown(i, e)}
                          @blur=${(e: Event) => this.commitAlias(i, e)}
                        />`
                      : html`<button
                          type="button"
                          class="r2m-button discover__add-alias"
                          @click=${() => this.beginAlias(i)}
                        >
                          ${icon('add')} Add alias
                        </button>`
                  }
                </div>
              </div>
            </div>`,
        )}
      </div>
    `;
  }

  async discover(): Promise<void> {
    const run = ++this.run;
    this.error.set(null);
    this.phase.set('discovering');
    try {
      const outcome = await this.discovery.discover(this.data.folder, this.thinking());
      if (run !== this.run) return;
      const failure = discoveryFailureMessage(outcome);
      if (failure) {
        // Stay open on failure: the error and Re-run are the point — a failed pass is exactly
        // when the user reaches for thinking.
        this.error.set(failure);
        this.phase.set('failed');
        return;
      }
      this.rows.set(toDiscoveryRows(outcome));
      this.phase.set('review');
    } catch (e) {
      if (run !== this.run) return;
      this.error.set(discoveryErrorMessage(toApiError(e)));
      this.phase.set('failed');
    }
  }

  patch(index: number, change: Partial<DiscoveryRow>): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  setAll(included: boolean): void {
    this.rows.update((rows) => rows.map((r) => ({ ...r, included })));
  }

  removeAlias(index: number, alias: string): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? withoutAlias(r, alias) : r)));
  }

  beginAlias(index: number): void {
    this.#focusAliasPending = true;
    this.addingAliasFor.set(index);
  }

  private onAliasKeydown(index: number, e: KeyboardEvent): void {
    if (e.key === 'Enter') {
      e.preventDefault();
      this.commitAlias(index, e);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      this.addingAliasFor.set(null);
    }
  }

  /** Enter and blur both land here; the first one closes the input, so the other is a no-op. */
  commitAlias(index: number, event: Event): void {
    if (this.addingAliasFor() !== index) return;
    const alias = (event.target as HTMLInputElement).value;
    this.rows.update((rows) => rows.map((r, i) => (i === index ? withAlias(r, alias) : r)));
    this.addingAliasFor.set(null);
  }

  async apply(): Promise<void> {
    const rows = applyRows(this.rows());
    if (rows.length === 0) return;
    this.applying.set(true);
    try {
      const response = await this.discovery.apply(this.data.folder, rows);
      const result: DiscoveryDialogResult = { applied: response.applied };
      this.emit('r2m-close', result);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.applying.set(false);
    }
  }
}
define('r2m-discovery-dialog', DiscoveryDialog);

/** Opens the discovery dialog; resolves how many rows were applied, or null when closed without applying. */
export async function openDiscoveryDialog(
  data: DiscoveryDialogData,
): Promise<DiscoveryDialogResult | null> {
  const dialog = document.createElement('r2m-discovery-dialog');
  dialog.data = data;
  return (await openDialog<DiscoveryDialogResult | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-discovery-dialog': DiscoveryDialog;
  }
}
