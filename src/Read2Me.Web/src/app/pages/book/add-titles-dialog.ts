import { html, nothing } from 'lit-html';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { adviceLine } from '@app/ui/partials';
import { type BookShape, TITLE_LEVELS, type TitleLevel, availableLevels } from './add-titles';
import addTitlesCss from './add-titles-dialog.css' with { type: 'text' };

adoptStyles(addTitlesCss);

/** What the dialog needs: the book's shape, and the `addTitles` advice for above its button. */
export interface AddTitlesDialogData extends BookShape {
  advice: string | null;
}

/**
 * Add titles (guidance-primitives spec §5): one box per level that adds navigation, all ticked.
 * The filled-in dialog is its own permanent-change confirm. Closes with the ticked levels in send
 * order, or null on Cancel.
 */
export class AddTitlesDialog extends R2mElement {
  data!: AddTitlesDialogData;
  readonly #unticked = signal<ReadonlySet<TitleLevel>>(new Set());
  readonly #available = computed(() => availableLevels(this.data));
  readonly #ticked = computed(() =>
    TITLE_LEVELS.map((l) => l.level).filter(
      (level) => this.#available()[level] && !this.#unticked().has(level),
    ),
  );

  protected override connected(): void {
    this.classList.add('titles');
  }

  protected template() {
    const available = this.#available();
    const unticked = this.#unticked();
    return html`
      <h2 class="r2m-dialog__title">Add titles</h2>
      <div class="r2m-dialog__content titles__content">
        <p class="titles__hint">
          Inserts a spoken title paragraph at the start of each level you tick.
        </p>
        ${TITLE_LEVELS.map((l) =>
          available[l.level]
            ? html`<label class="r2m-checkbox">
                  <input
                    type="checkbox"
                    name=${l.level}
                    .checked=${!unticked.has(l.level)}
                    @change=${(e: Event) => this.#set(l.level, (e.target as HTMLInputElement).checked)}
                  />
                  ${l.label}
                </label>
                ${unticked.has(l.level) ? html`<p class="titles__lost">${l.lost}</p>` : nothing}`
            : html`<p class="titles__missing">${l.missing}</p>`,
        )}
        <div class="titles__advice">${adviceLine(this.data.advice)}</div>
      </div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button"
          data-action="cancel"
          @click=${() => this.emit('r2m-close', null)}
        >
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="apply-titles"
          ?disabled=${this.#ticked().length === 0}
          @click=${() => this.emit('r2m-close', this.#ticked())}
        >
          Add titles
        </button>
      </div>
    `;
  }

  #set(level: TitleLevel, on: boolean): void {
    this.#unticked.update((s) => {
      const next = new Set(s);
      if (on) next.delete(level);
      else next.add(level);
      return next;
    });
  }
}
define('r2m-add-titles-dialog', AddTitlesDialog);

/** Opens the dialog; resolves the ticked levels in send order, or null when cancelled. */
export async function openAddTitlesDialog(data: AddTitlesDialogData): Promise<TitleLevel[] | null> {
  const dialog = document.createElement('r2m-add-titles-dialog');
  dialog.data = data;
  return (await openDialog<TitleLevel[] | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-add-titles-dialog': AddTitlesDialog;
  }
}
