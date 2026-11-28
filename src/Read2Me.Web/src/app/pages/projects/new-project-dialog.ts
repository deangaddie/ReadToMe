import { html, nothing } from 'lit-html';
import { BOOK_ACCEPT, BOOK_MAX_BYTES, toApiError } from '@app/api';
import { type ClosableContent, openDialog, requestDialogClose } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { controlValue, field } from '@app/ui/field';
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

/** The fields in reading order: a failed submit focuses the first one with an error. */
const FIELD_ORDER: readonly DraftField[] = ['bookTitle', 'title', 'author', 'file'];

/**
 * New project dialog (design §6.1): book title, project title (follows the book title until
 * edited), author and an epub/txt drop. A form: Create stays enabled (disabled only while the
 * upload runs), and submitting an invalid draft shows every error and focuses the first invalid
 * field. A field's error shows once it is blurred (the drop: once a file is picked or refused) or
 * the form submitted, never while typing. The upload runs here so a host rejection (duplicate
 * title, unreadable file) shows inline and the form survives. Closes with the new project's folder name; Cancel and Escape close with
 * nothing, a backdrop click does not (`closedBy: 'closerequest'`: the second click of a double-click
 * in the OS file picker can land on the backdrop). While the upload runs it refuses to close.
 */
export class NewProjectDialog extends R2mElement implements ClosableContent {
  private readonly store = use(ProjectsStore);

  readonly #draft = signal<NewProjectDraft>({ ...EMPTY_DRAFT });
  readonly #touched = signal<Partial<Record<DraftField, boolean>>>({});
  readonly #errors = computed<NewProjectErrors>(() => validateDraft(this.#draft()));
  readonly #valid = computed(() => isDraftValid(this.#draft()));
  readonly #busy = signal(false);
  readonly #submitError = signal<string | null>(null);

  protected override connected(): void {
    this.classList.add('new-project');
  }

  protected template() {
    const draft = this.#draft();
    const busy = this.#busy();
    const submitError = this.#submitError();
    const fileError = draft.fileError;
    const fileMissing = this.#error('file');
    return html`
      <h2 class="r2m-dialog__title">New project</h2>
      <form novalidate @submit=${(e: SubmitEvent) => this.#onSubmit(e)}>
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
                ? statusChip({
                    status: 'ok',
                    icon: 'description',
                    label: draft.file.name,
                    compact: true,
                  })
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
            type="submit"
            class="r2m-button r2m-button--filled new-project__create"
            ?disabled=${busy}
          >
            ${icon('add')} Create
          </button>
        </div>
      </form>
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
    return field(
      {
        id: `new-project-${name}`,
        label,
        hint,
        error: this.#error(name),
        cls: 'new-project__field',
      },
      (a) =>
        html`<input
          id=${a.id}
          type="text"
          name=${name}
          required
          ?autofocus=${autofocus}
          .value=${value}
          aria-invalid=${a.invalid}
          aria-describedby=${a.describedBy}
          @input=${(e: Event) => onInput(controlValue(e))}
          @blur=${() => this.#touch(name)}
        />`,
    );
  }

  #error(key: DraftField): string | null {
    return this.#touched()[key] ? (this.#errors()[key] ?? null) : null;
  }

  #setBookTitle(value: string): void {
    this.#draft.update((d) => setBookTitle(d, value));
  }

  #setTitle(value: string): void {
    this.#draft.update((d) => setTitle(d, value));
  }

  #setAuthor(value: string): void {
    this.#draft.update((d) => setAuthor(d, value));
  }

  onFiles(files: File[]): void {
    const file = files[0];
    if (file) this.#draft.update((d) => setFile(d, file));
    this.#touch('file');
  }

  onRejected(rejected: RejectedFile[]): void {
    const first = rejected[0];
    if (first) this.#draft.update((d) => rejectFile(d, first));
    this.#touch('file');
  }

  #onSubmit(e: SubmitEvent): void {
    e.preventDefault();
    void this.create();
  }

  /** Submit: every field touched; an invalid draft focuses its first invalid field and sends nothing. */
  async create(): Promise<void> {
    if (this.#busy()) return;
    this.#touched.set({ bookTitle: true, title: true, author: true, file: true });
    if (!this.#valid()) {
      await this.rendered();
      this.#firstInvalid()?.focus();
      return;
    }
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

  /** The upload must finish: Escape and Cancel are refused while it runs. */
  canClose(): boolean {
    return !this.#busy();
  }

  cancel(): void {
    requestDialogClose(this);
  }

  /** The control of the first field in reading order with an error; the file's is the drop's Choose file. */
  #firstInvalid(): HTMLElement | null {
    const errors = this.#errors();
    const key = FIELD_ORDER.find((k) => errors[k]);
    if (!key) return null;
    return key === 'file'
      ? this.querySelector<HTMLElement>('r2m-file-drop button')
      : this.querySelector<HTMLElement>(`input[name="${key}"]`);
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
  return (await openDialog<string | null>(dialog, { closedBy: 'closerequest' })) ?? undefined;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-new-project-dialog': NewProjectDialog;
  }
}
