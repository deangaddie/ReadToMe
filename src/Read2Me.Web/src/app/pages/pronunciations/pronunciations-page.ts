import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { type Guid, type PronunciationDto, PronunciationsApi, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { hasFacet } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { Debounced } from '@app/shared/debounced';
import { confirmDiscard } from '@app/shared/unsaved-changes';
import { ConfirmService } from '@app/ui/dialogs';
import { blockable, emptyState, icon, spinner, statusChip } from '@app/ui/partials';
import { ProjectStore } from '../project/project-store';
import type { PronunciationEditor } from './pronunciation-editor';
import { deleteBlockedReason, filterEntries } from './pronunciation-logic';
import { PronunciationWriter } from './pronunciation-writer';
import css from './pronunciations-page.css' with { type: 'text' };
import './pronunciation-editor';

adoptStyles(css);

/** What the editor pane holds: a new entry, an existing one by id, or nothing. */
type Editing = { kind: 'new' } | { kind: 'entry'; id: Guid } | null;

const editingKey = (editing: Editing): string =>
  editing === null ? '' : editing.kind === 'new' ? 'new' : editing.id;

/**
 * `/projects/{folder}/pronunciations` (ticket 12, variant F without the try-out): the book's
 * Pronunciation list on the left — Add pronunciation, search, and a Written | Spoken | Items |
 * With audio table with a delete per row — and the one entry being edited on the right, in the
 * shared {@link PronunciationEditor}. On narrow screens the editor replaces the list. The list
 * rereads on every receipt carrying the `Pronunciations` facet (any tab's or agent's change), and
 * at the viewed step while one is viewed (read-only then).
 */
export class PronunciationsPage extends R2mElement {
  private readonly api = use(PronunciationsApi);
  private readonly live = use(LiveService);
  private readonly writer = use(PronunciationWriter);
  private readonly confirm = use(ConfirmService);
  private readonly router = use(Router);
  private project!: ProjectStore;

  readonly entries = signal<PronunciationDto[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly query = signal('');
  readonly editing = signal<Editing>(null);

  private readonly visible = computed(() => filterEntries(this.entries(), this.query()));
  /** The entry open in the editor, from the latest list read; null for a new one or a vanished one. */
  private readonly openEntry = computed(() => {
    const editing = this.editing();
    return editing?.kind === 'entry'
      ? (this.entries().find((e) => e.id === editing.id) ?? null)
      : null;
  });
  private readonly refetch = new Debounced(() => this.load());

  protected override connected(): void {
    this.classList.add('pron-page');
    this.project = use(ProjectStore, this);
    this.effect(() => {
      const folder = this.project.folder();
      if (!folder) return;
      untracked(() => void this.load());
      return this.live.receipts(folder, (r) => {
        if (hasFacet(r.effects.facets, 'Pronunciations')) this.refetch.schedule();
      });
    });
    this.onDisconnect(() => this.refetch.cancel());
    // `?entry=` (the Book view's Edit pencil): open that entry once the list holds it, then drop the param.
    this.effect(() => {
      const id = this.router.query().get('entry');
      if (id === null || !this.entries().some((e) => e.id === id)) return;
      untracked(() => {
        this.editing.set({ kind: 'entry', id });
        const query = Object.fromEntries(this.router.query());
        this.router.navigate(this.router.path(), {
          replace: true,
          query: { ...query, entry: null },
        });
      });
    });
  }

  /** The router's `guardUnsaved` asks before leaving with an unsaved draft. */
  hasUnsavedChanges(): boolean {
    return this.querySelector<PronunciationEditor>('r2m-pronunciation-editor')?.dirty() === true;
  }

  private async load(): Promise<void> {
    const folder = this.project.folder();
    if (!folder) return;
    this.loading.set(true);
    try {
      const entries = await this.api.list(folder);
      if (this.project.folder() !== folder) return;
      this.entries.set(entries);
      this.error.set(null);
      // An entry deleted elsewhere leaves nothing to edit.
      const editing = this.editing();
      if (editing?.kind === 'entry' && !entries.some((e) => e.id === editing.id)) {
        this.editing.set(null);
      }
    } catch (e) {
      if (this.project.folder() === folder) this.error.set(toApiError(e).message);
    } finally {
      if (this.project.folder() === folder) this.loading.set(false);
    }
  }

  private async open(next: Editing): Promise<void> {
    if (editingKey(next) === editingKey(this.editing())) return;
    if (this.hasUnsavedChanges() && !(await confirmDiscard(this.confirm))) return;
    this.editing.set(next);
  }

  private async deleteEntry(entry: PronunciationDto): Promise<void> {
    const folder = this.project.folder();
    if (!folder) return;
    if ((await this.writer.remove(folder, entry)) && this.openEntry()?.id === entry.id) {
      this.editing.set(null);
    }
  }

  protected template() {
    const folder = this.project.folder();
    const readOnly = this.project.readOnly();
    const audioInFlight = this.project.audioInFlight();
    const deleteReason = deleteBlockedReason({ readOnly, audioInFlight });
    const editing = this.editing();
    const openEntry = this.openEntry();
    const entries = this.entries();
    const visible = this.visible();
    const error = this.error();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Pronunciations</h1>
        <p class="r2m-page-header__subtitle">
          How the book's made-up words are spoken: each written form is replaced by its spoken form
          before the text goes to TTS. The book text never changes.
        </p>
      </header>

      ${
        error
          ? statusChip({
              status: 'error',
              label: `Pronunciations could not be loaded: ${error}`,
            })
          : nothing
      }

      <div class="pron ${editing ? 'pron--editing' : ''}">
        <section class="pron__list" aria-label="Pronunciation list">
          <div class="pron__toolbar">
            ${blockable(
              readOnly,
              html`<button
                type="button"
                class="r2m-button r2m-button--filled"
                data-action="add"
                ?disabled=${readOnly !== null}
                @click=${() => void this.open({ kind: 'new' })}
              >
                ${icon('add')} Add pronunciation
              </button>`,
            )}
            <label class="r2m-field pron__search">
              <input
                type="search"
                placeholder="Search"
                aria-label="Search pronunciations"
                .value=${this.query()}
                @input=${(e: Event) => this.query.set((e.target as HTMLInputElement).value)}
              />
            </label>
          </div>
          ${
            this.loading() && entries.length === 0
              ? html`<p class="pron__none">${spinner(20)} Loading…</p>`
              : entries.length === 0
                ? emptyState(
                    {
                      icon: 'text_to_speech',
                      headline: 'No pronunciations yet',
                      hint: 'Add a word the TTS gets wrong, written as the book spells it and spoken as it should sound.',
                      compact: true,
                    },
                    readOnly === null
                      ? [
                          {
                            label: 'Add pronunciation',
                            icon: 'add',
                            run: () => void this.open({ kind: 'new' }),
                          },
                        ]
                      : [],
                  )
                : visible.length === 0
                  ? html`<p class="pron__none">
                      No pronunciation matches "${this.query().trim()}".
                    </p>`
                  : html`<table class="pron__table">
                      <thead>
                        <tr>
                          <th scope="col">Written</th>
                          <th scope="col">Spoken</th>
                          <th scope="col" class="pron__num">Items</th>
                          <th scope="col" class="pron__num">With audio</th>
                          <th scope="col" aria-label="Delete"></th>
                        </tr>
                      </thead>
                      <tbody>
                        ${repeat(
                          visible,
                          (e) => e.id,
                          (e) =>
                            html`<tr
                              class="pron__row ${openEntry?.id === e.id ? 'pron__row--selected' : ''}"
                              data-id=${e.id}
                              @click=${() => void this.open({ kind: 'entry', id: e.id })}
                            >
                              <td class="pron__written">
                                <button
                                  type="button"
                                  class="pron__open"
                                  aria-label="Edit ${e.writtenForm}"
                                  @click=${(ev: Event) => {
                                    ev.stopPropagation();
                                    void this.open({ kind: 'entry', id: e.id });
                                  }}
                                >
                                  ${e.writtenForm}
                                </button>
                              </td>
                              <td class="pron__spoken">${e.spokenForm}</td>
                              <td class="pron__num">${e.itemCount}</td>
                              <td class="pron__num">${e.withAudioCount || '–'}</td>
                              <td
                                class="pron__delete"
                                @click=${(ev: Event) => ev.stopPropagation()}
                              >
                                ${blockable(
                                  deleteReason,
                                  html`<button
                                    type="button"
                                    class="r2m-icon-button"
                                    data-action="delete-row"
                                    aria-label="Delete ${e.writtenForm}"
                                    ?disabled=${deleteReason !== null}
                                    @click=${() => void this.deleteEntry(e)}
                                  >
                                    ${icon('delete')}
                                  </button>`,
                                )}
                              </td>
                            </tr>`,
                        )}
                      </tbody>
                    </table>`
          }
        </section>
        <section class="pron__editor" aria-label="Edit pronunciation">
          ${
            editing && folder
              ? html`<r2m-pronunciation-editor
                  .folder=${folder}
                  .entry=${openEntry}
                  .readOnly=${readOnly}
                  .audioInFlight=${audioInFlight}
                  @editor-close=${() => this.editing.set(null)}
                ></r2m-pronunciation-editor>`
              : html`<p class="pron__hint">Pick a pronunciation to edit it, or add one.</p>`
          }
        </section>
      </div>
    `;
  }
}
define('r2m-pronunciations-page', PronunciationsPage);
