import { html, nothing } from 'lit-html';
import { BOOK_ACCEPT, BOOK_MAX_BYTES, toApiError } from '@app/api';
import { openDialog, setDialogClosedBy } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import type { RejectedFile } from '@app/ui/file-drop';
import { icon, statusChip } from '@app/ui/partials';
import {
  EMPTY_DRAFT,
  type NewProjectDraft,
  type NewProjectErrors,
  isDraftValid,
  rejectFile,
  setAuthor,
  setBookTitle,
  setFile,
  setTitle,
  toCreateRequest,
  validateDraft,
} from './new-project-form';
import { ProjectsStore } from './projects-store';
import '@app/ui/file-drop';
import newProjectCss from './new-project-dialog.css' with { type: 'text' };

adoptStyles(newProjectCss);

type DraftField = keyof NewProjectErrors;

/**
 * New project dialog (design §6.1): book title, project title (follows the book title until
 * edited), author and an epub/txt drop. Create stays disabled until every field is valid; the
 * upload runs here so a host rejection (duplicate title, unreadable file) shows inline and the
 * form survives. Closes with the new project's folder name; Cancel and dismissal close with
 * nothing. While the upload runs the dialog refuses to close.
 */
export class NewProjectDialog extends R2mElement {
  private readonly store = use(ProjectsStore);

  readonly #draft = signal<NewProjectDraft>({ ...EMPTY_DRAFT });
  readonly #touched = signal<Partial<Record<DraftField, boolean>>>({});
  readonly #errors = computed<NewProjectErrors>(() => validateDraft(this.#draft()));
  readonly #valid = computed(() => isDraftValid(this.#draft()));
  readonly #busy = signal(false);
  readonly #submitError = signal<string | null>(null);

  protected override connected(): void {
    this.classList.add('new-project');
    // The upload must finish: no Escape or backdrop dismissal while it runs (MatDialog's disableClose).
    this.effect(() => setDialogClosedBy(this, this.#busy() ? 'none' : 'any'));
  }

  protected template() {
    const draft = this.#draft();
    const busy = this.#busy();
    const submitError = this.#submitError();
    const fileError = draft.fileError;
    const fileMissing = this.#error('file');
    return html`
      <h2 class="r2m-dialog__title">New project</h2>
      <div class="r2m-dialog__content new-project__content">
        ${this.#field('bookTitle', 'Book title', draft.bookTitle, (v) => this.#setBookTitle(v), true)}
        ${this.#field(
          'title',
          'Project title',
          draft.title,
          (v) => this.#setTitle(v),
          false,
          'Names the project folder. Follows the book title until you change it.',
        )}
        ${this.#field('author', 'Author', draft.author, (v) => this.#setAuthor(v))}

        <r2m-file-drop
          .accept=${BOOK_ACCEPT}
          .maxBytes=${BOOK_MAX_BYTES}
          .label=${'Drop the book here'}
          .hint=${'epub or txt, up to 100 MB'}
          @files=${(e: Event) => this.onFiles((e as CustomEvent<File[]>).detail)}
          @rejected=${(e: Event) => this.onRejected((e as CustomEvent<RejectedFile[]>).detail)}
        ></r2m-file-drop>

        <div class="new-project__file">
          ${
            draft.file
              ? statusChip({ status: 'ok', icon: 'description', label: draft.file.name, compact: true })
              : nothing
          }
          ${
            fileError
              ? statusChip({ status: 'error', label: fileError, compact: true })
              : fileMissing
                ? statusChip({ status: 'warn', label: fileMissing, compact: true })
                : nothing
          }
        </div>

        ${
          submitError
            ? html`<div class="new-project__submit-error">
                ${statusChip({ status: 'error', label: submitError })}
              </div>`
            : nothing
        }
      </div>

      ${busy ? html`<progress aria-label="Uploading"></progress>` : nothing}

      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button new-project__cancel"
          ?disabled=${busy}
          @click=${() => this.cancel()}
        >
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled new-project__create"
          ?disabled=${!this.#valid() || busy}
          @click=${() => void this.create()}
        >
          ${icon('add')} Create
        </button>
      </div>
    `;
  }

  #field(
    name: DraftField,
    label: string,
    value: string,
    onInput: (value: string) => void,
    autofocus = false,
    hint?: string,
  ) {
    const error = this.#error(name);
    const describedBy = `new-project-${name}-help`;
    const help = error ?? (hint || null);
    return html`<label class="r2m-field new-project__field">
      <span class="r2m-field__label">${label}</span>
      <input
        type="text"
        name=${name}
        required
        ?autofocus=${autofocus}
        .value=${value}
        aria-invalid=${error ? 'true' : nothing}
        aria-describedby=${help ? describedBy : nothing}
        @input=${(e: Event) => onInput((e.target as HTMLInputElement).value)}
      />
      ${
        help
          ? html`<span
              id=${describedBy}
              class=${error ? 'r2m-field__error' : 'r2m-field__hint'}
              role=${error ? 'alert' : nothing}
              >${help}</span
            >`
          : nothing
      }
    </label>`;
  }

  #error(key: DraftField): string | null {
    return this.#touched()[key] ? (this.#errors()[key] ?? null) : null;
  }

  #setBookTitle(value: string): void {
    this.#draft.update((d) => setBookTitle(d, value));
    this.#touch('bookTitle');
  }

  #setTitle(value: string): void {
    this.#draft.update((d) => setTitle(d, value));
    this.#touch('title');
  }

  #setAuthor(value: string): void {
    this.#draft.update((d) => setAuthor(d, value));
    this.#touch('author');
  }

  onFiles(files: File[]): void {
    const file = files[0];
    if (file) this.#draft.update((d) => setFile(d, file));
  }

  onRejected(rejected: RejectedFile[]): void {
    const first = rejected[0];
    if (first) this.#draft.update((d) => rejectFile(d, first));
  }

  async create(): Promise<void> {
    this.#touched.set({ bookTitle: true, title: true, author: true, file: true });
    if (!this.#valid() || this.#busy()) return;
    this.#busy.set(true);
    this.#submitError.set(null);
    try {
      const folder = await this.store.create(toCreateRequest(this.#draft()));
      this.emit('r2m-close', folder);
    } catch (error) {
      this.#submitError.set(toApiError(error).toProblem().detail ?? 'Could not create the project');
    } finally {
      this.#busy.set(false);
    }
  }

  cancel(): void {
    if (!this.#busy()) this.emit('r2m-close', undefined);
  }

  #touch(key: DraftField): void {
    this.#touched.update((t) => ({ ...t, [key]: true }));
  }
}
define('r2m-new-project-dialog', NewProjectDialog);

/** Opens the dialog; resolves the new project's folder name, or undefined when cancelled. */
export async function openNewProjectDialog(): Promise<string | undefined> {
  const dialog = document.createElement('r2m-new-project-dialog');
  // A CustomEvent turns an undefined detail into null; Cancel and dismissal both mean "nothing".
  return (await openDialog<string | null>(dialog)) ?? undefined;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-new-project-dialog': NewProjectDialog;
  }
}
