import type { Block } from '@app/api';
import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { gated, icon } from './partials';

export type FileRejectReason = 'type' | 'size';

export interface RejectedFile {
  file: File;
  reason: FileRejectReason;
}

/**
 * Drag-and-drop + button file input with accept/max-size validation (design §7). `accept` takes
 * the same syntax as the native attribute (`.epub,.txt`, `audio/*`, `image/png`). Accepted files
 * go out on `files`, the rest on `rejected` with a reason. The hidden `<input type=file>` keeps
 * the `.r2m-file-drop__input` hook so a browser test can set files on it.
 */
export class FileDrop extends R2mElement {
  readonly #accept = signal('');
  /** Native `accept` syntax: comma-separated extensions and/or MIME types (wildcards allowed). */
  get accept() {
    return this.#accept();
  }
  set accept(value: string) {
    this.#accept.set(value);
  }

  readonly #maxBytes = signal<number | undefined>(undefined);
  get maxBytes() {
    return this.#maxBytes();
  }
  set maxBytes(value: number | undefined) {
    this.#maxBytes.set(value);
  }

  readonly #multiple = signal(false);
  get multiple() {
    return this.#multiple();
  }
  set multiple(value: boolean) {
    this.#multiple.set(value);
  }

  readonly #label = signal('Drop a file here');
  get label() {
    return this.#label();
  }
  set label(value: string) {
    this.#label.set(value);
  }

  readonly #hint = signal<string | undefined>(undefined);
  get hint() {
    return this.#hint();
  }
  set hint(value: string | undefined) {
    this.#hint.set(value);
  }

  /** The zone's Edit gate (viewing a History step, say): while blocked the picker is off with its reason and a drop is ignored. */
  readonly #block = signal<Block | null>(null);
  get block() {
    return this.#block();
  }
  set block(value: Block | null) {
    this.#block.set(value);
  }

  readonly #over = signal(false);

  readonly #rules = computed(() =>
    this.#accept()
      .split(',')
      .map((r) => r.trim().toLowerCase())
      .filter((r) => r.length > 0),
  );

  protected override connected(): void {
    this.classList.add('r2m-file-drop');
    this.effect(() => this.classList.toggle('r2m-file-drop--over', this.#over()));
    this.effect(() => this.classList.toggle('r2m-file-drop--read-only', this.#block() !== null));
    // Blocked: no preventDefault, so the browser shows a no-drop cursor and never fires `drop`.
    const onDragOver = (e: DragEvent) => {
      if (this.#block() !== null) return;
      e.preventDefault();
      this.#over.set(true);
    };
    const onDragLeave = () => this.#over.set(false);
    const onDrop = (e: DragEvent) => {
      e.preventDefault();
      this.#over.set(false);
      if (this.#block() === null) this.#take(Array.from(e.dataTransfer?.files ?? []));
    };
    this.addEventListener('dragover', onDragOver);
    this.addEventListener('dragleave', onDragLeave);
    this.addEventListener('drop', onDrop);
    this.onDisconnect(() => {
      this.removeEventListener('dragover', onDragOver);
      this.removeEventListener('dragleave', onDragLeave);
      this.removeEventListener('drop', onDrop);
    });
  }

  protected template() {
    const hint = this.#hint();
    const block = this.#block();
    return html`
      ${icon('upload_file', 'r2m-file-drop__icon')}
      <p class="r2m-file-drop__label">${this.#label()}</p>
      ${hint ? html`<p class="r2m-file-drop__hint">${hint}</p>` : nothing}
      ${gated(
        block,
        (disabled) =>
          html`<button
            type="button"
            class="r2m-button r2m-button--stroked"
            ?disabled=${disabled}
            @click=${() => this.querySelector<HTMLInputElement>('.r2m-file-drop__input')?.click()}
          >
            ${this.#multiple() ? 'Choose files' : 'Choose file'}
          </button>`,
      )}
      <input
        class="r2m-file-drop__input"
        type="file"
        accept=${this.#accept()}
        ?multiple=${this.#multiple()}
        ?disabled=${block !== null}
        tabindex="-1"
        aria-hidden="true"
        @change=${(e: Event) => this.#onPicked(e.currentTarget as HTMLInputElement)}
      />
    `;
  }

  #onPicked(picker: HTMLInputElement): void {
    this.#take(Array.from(picker.files ?? []));
    picker.value = '';
  }

  #take(all: File[]): void {
    const candidates = this.#multiple() ? all : all.slice(0, 1);
    const accepted: File[] = [];
    const rejected: RejectedFile[] = [];
    for (const file of candidates) {
      const reason = this.#reject(file);
      if (reason) rejected.push({ file, reason });
      else accepted.push(file);
    }
    if (accepted.length) this.emit('files', accepted);
    if (rejected.length) this.emit('rejected', rejected);
  }

  #reject(file: File): FileRejectReason | null {
    if (!this.#matchesAccept(file)) return 'type';
    const max = this.#maxBytes();
    if (max !== undefined && file.size > max) return 'size';
    return null;
  }

  #matchesAccept(file: File): boolean {
    const rules = this.#rules();
    if (rules.length === 0) return true;
    const name = file.name.toLowerCase();
    const mime = file.type.toLowerCase();
    return rules.some((rule) => {
      if (rule.startsWith('.')) return name.endsWith(rule);
      if (rule.endsWith('/*')) return mime.startsWith(rule.slice(0, -1));
      return mime === rule;
    });
  }
}
define('r2m-file-drop', FileDrop);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-file-drop': FileDrop;
  }
}
