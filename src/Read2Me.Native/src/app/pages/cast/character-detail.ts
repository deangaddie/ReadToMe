import { html, nothing } from 'lit-html';
import type { CharacterLineDto, CharacterSummaryDto } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { type ReadonlySignal, computed, signal } from '@app/core/signals';
import { ConfirmService } from '@app/ui/dialogs';
import { icon, statusChip } from '@app/ui/partials';
import { castPath } from './cast-path';
import { CastStore } from './cast-store';
import { openMergeDialog } from './merge-dialog';
import '@app/ui/inline-edit';
import './character-lines';

/** The extra note on Delete when the character narrates the book (research §4). */
export function linkedNarratorDeleteMessage(name: string): string {
  return `${name} narrates this book; deleting will return narration to the Narrator voice.`;
}

/**
 * The right-hand panel (research §4 "Detail header", "Aliases", "Lines"): rename, Merge and
 * Delete — all hidden on the seed Narrator — the alias chips, and the lines list. Every write
 * goes through the {@link CastStore} the page provides; the panel re-renders from the roster it
 * reloads. The Voices and Voice rules sections arrive with the voices screens (native-web 31).
 */
export class CharacterDetail extends R2mElement {
  private readonly confirm = use(ConfirmService);
  private readonly router = use(Router);
  private store!: CastStore;

  readonly #character = signal<CharacterSummaryDto | null>(null);
  get character() {
    return this.#character();
  }
  set character(value: CharacterSummaryDto | null) {
    this.#character.set(value);
  }

  readonly addingAlias = signal(false);
  /** The alias input was just opened: focus it after that render. */
  #focusAliasPending = false;

  /** Built in `connected()`, once the page's store is reachable through the DOM. */
  private mergeTargets!: ReadonlySignal<number>;
  private folder!: ReadonlySignal<string>;

  protected override connected(): void {
    this.classList.add('character-detail');
    this.store = use(CastStore, this);
    this.folder = computed(() => this.store.folder() ?? '');
    this.mergeTargets = computed(
      () => this.store.rows().filter((r) => !r.isNarrator && r.id !== this.#character()?.id).length,
    );
  }

  protected override updated(): void {
    if (!this.#focusAliasPending) return;
    this.#focusAliasPending = false;
    this.querySelector<HTMLInputElement>('.character-detail__alias-input')?.focus();
  }

  protected template() {
    const character = this.#character();
    if (!character) return nothing;
    const busy = this.store.busy();
    return html`
      <header class="character-detail__header">
        ${
          character.isNarrator
            ? html`<h2 class="character-detail__name">${character.name}</h2>`
            : html`<r2m-inline-edit
                  class="character-detail__name"
                  .value=${character.name}
                  .placeholder=${'Character name'}
                  .required=${true}
                  @save=${(e: Event) => void this.rename((e as CustomEvent<string>).detail)}
                ></r2m-inline-edit>
                <button
                  type="button"
                  class="r2m-button r2m-button--stroked"
                  data-action="merge"
                  ?disabled=${busy || this.mergeTargets() === 0}
                  @click=${() => void this.merge()}
                >
                  ${icon('merge')} Merge
                </button>
                <button
                  type="button"
                  class="r2m-button r2m-button--stroked character-detail__delete"
                  data-action="delete"
                  ?disabled=${busy}
                  @click=${() => void this.deleteCharacter()}
                >
                  ${icon('delete')} Delete
                </button>`
        }
      </header>

      ${
        character.narratesBook
          ? statusChip({
              status: 'info',
              icon: 'menu_book',
              label: 'Narrates this book',
              compact: true,
            })
          : nothing
      }
      ${character.isNarrator ? nothing : this.aliases(character, busy)}

      <section class="character-detail__section" aria-label="Lines">
        <h3 class="character-detail__heading">
          Lines <span class="character-detail__count">${character.lineCount}</span>
        </h3>
        <r2m-character-lines
          .folder=${this.folder()}
          .lines=${this.store.lines()}
          .loading=${this.store.linesLoading()}
          @open=${(e: Event) => this.openInReader((e as CustomEvent<CharacterLineDto>).detail)}
        ></r2m-character-lines>
      </section>
    `;
  }

  private aliases(character: CharacterSummaryDto, busy: boolean) {
    return html`<section class="character-detail__section" aria-label="Aliases">
      <h3 class="character-detail__heading">Aliases</h3>
      <div class="character-detail__aliases">
        ${character.aliases.map(
          (alias) =>
            html`<span class="character-detail__alias" data-alias=${alias.name}>
              ${alias.name}
              <button
                type="button"
                class="character-detail__alias-remove"
                aria-label=${`Remove alias ${alias.name}`}
                ?disabled=${busy}
                @click=${() => void this.removeAlias(alias.id)}
              >
                ${icon('close')}
              </button>
            </span>`,
        )}
        ${
          this.addingAlias()
            ? html`<input
                class="character-detail__alias-input"
                type="text"
                placeholder="Alias name"
                aria-label="New alias"
                @keydown=${this.#onAliasKeydown}
                @blur=${(e: Event) => void this.commitAlias(e)}
              />`
            : html`<button
                type="button"
                class="r2m-button"
                data-action="add-alias"
                ?disabled=${busy}
                @click=${() => this.beginAlias()}
              >
                ${icon('add')} Add alias
              </button>`
        }
      </div>
    </section>`;
  }

  readonly #onAliasKeydown = (e: KeyboardEvent): void => {
    if (e.key === 'Enter') {
      e.preventDefault();
      void this.commitAlias(e);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      this.addingAlias.set(false);
    }
  };

  beginAlias(): void {
    this.#focusAliasPending = true;
    this.addingAlias.set(true);
  }

  async rename(name: string): Promise<void> {
    const character = this.#character();
    if (!character || name === character.name) return;
    await this.store.run({ type: 'RenameCharacter', characterId: character.id, name });
  }

  async removeAlias(aliasId: string): Promise<void> {
    await this.store.run({ type: 'RemoveCharacterAlias', aliasId });
  }

  /** Enter and blur both land here; the first one closes the input, so the other is a no-op. */
  async commitAlias(event: Event): Promise<void> {
    if (!this.addingAlias()) return;
    const character = this.#character();
    const name = (event.target as HTMLInputElement).value.trim();
    this.addingAlias.set(false);
    if (!name || !character) return;
    await this.store.run({ type: 'AddCharacterAlias', characterId: character.id, name });
  }

  async merge(): Promise<void> {
    const merged = this.#character();
    if (!merged) return;
    const choice = await openMergeDialog({
      folder: this.folder(),
      merged,
      rows: this.store.rows(),
    });
    if (!choice) return;
    const response = await this.store.run({
      type: 'MergeCharacters',
      survivorId: choice.survivorId,
      mergedId: merged.id,
      addNameAsAlias: choice.addNameAsAlias,
    });
    if (response) this.goTo(choice.survivorId);
  }

  async deleteCharacter(): Promise<void> {
    const character = this.#character();
    if (!character) return;
    const note = character.narratesBook ? ` ${linkedNarratorDeleteMessage(character.name)}` : '';
    const ok = await this.confirm.confirm({
      title: 'Delete character',
      message:
        `Delete ${character.name}? Its lines become unattributed and its voices are removed.` +
        note,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!ok) return;
    const response = await this.store.run({ type: 'DeleteCharacter', characterId: character.id });
    if (response) this.goTo(null);
  }

  /** Open the reader at the line's chapter in Speakers mode. */
  openInReader(line: CharacterLineDto): void {
    this.router.navigate(`projects/${encodeURIComponent(this.folder())}/book`, {
      query: { mode: 'speakers', chapter: line.chapterId },
    });
  }

  private goTo(id: string | null): void {
    this.router.navigate(castPath(this.folder(), id));
  }
}
define('r2m-character-detail', CharacterDetail);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-character-detail': CharacterDetail;
  }
}
