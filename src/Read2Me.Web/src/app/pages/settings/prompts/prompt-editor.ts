import { html, nothing } from 'lit-html';
import { type PromptCatalogEntry, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { configEditorFrame } from '@app/ui/config-editor-frame';
import { ConfirmService } from '@app/ui/dialogs';
import { field } from '@app/ui/field';
import { icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { canReset, insertToken, isDirty, tokenLiteral } from './prompt-editing';
import { PromptsStore } from './prompts-store';

/** How long the preview waits after the last keystroke before asking the host again. */
export const PREVIEW_DEBOUNCE_MS = 300;

/**
 * Detail side of the prompts page (ticket 23): one kind's description, compatibility warnings,
 * token chips that insert at the caret, the monospace template, the expected response and a
 * preview of the *unsaved* draft rendered by the host with its sample book. Save is dirty-gated;
 * Reset to default drops the stored override (or just the unsaved edits) after a confirm. Set
 * `entry`; `dirty()` tells the page whether edits would be lost.
 */
export class PromptEditor extends R2mElement {
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private store!: PromptsStore;

  readonly #entry = signal<PromptCatalogEntry | null>(null);
  get entry() {
    return this.#entry();
  }
  set entry(value: PromptCatalogEntry | null) {
    this.#entry.set(value);
  }

  readonly draft = signal('');
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly previewOpen = signal(false);
  /** Null while a rendering is on its way. */
  readonly preview = signal<string | null>(null);
  readonly previewError = signal<string | null>(null);
  private previewSeq = 0;
  private previewTimer: ReturnType<typeof setTimeout> | null = null;

  readonly dirty = computed(() => {
    const entry = this.#entry();
    return !!entry && isDirty(entry, this.draft());
  });
  readonly resettable = computed(() => {
    const entry = this.#entry();
    return !!entry && canReset(entry, this.draft());
  });

  protected override connected(): void {
    this.classList.add('prompt-editor');
    this.store = use(PromptsStore, this);
    // A new kind replaces the draft; a reload of the same kind replaces it only while the draft
    // is still the text this editor last showed — a save made in another tab or over the API
    // must not wipe typing in progress, but must reach an untouched editor.
    let shown: { kind: string; template: string } | null = null;
    this.effect(() => {
      const entry = this.#entry();
      if (!entry) return;
      untracked(() => {
        if (entry.kind !== shown?.kind) {
          this.draft.set(entry.template);
          this.error.set(null);
          this.previewOpen.set(false);
          this.preview.set(null);
        } else if (this.draft() === shown.template) {
          this.draft.set(entry.template);
        }
        shown = { kind: entry.kind, template: entry.template };
      });
    });
    // The preview follows the draft while the panel is open, after a short pause in typing.
    this.effect(() => {
      const open = this.previewOpen();
      const template = this.draft();
      const kind = this.#entry()?.kind;
      untracked(() => {
        if (kind) this.schedulePreview(open, kind, template);
      });
    });
    this.onDisconnect(() => this.clearPreviewTimer());
  }

  protected template() {
    const entry = this.#entry();
    if (!entry) return nothing;
    return configEditorFrame(
      {
        title: entry.title,
        dirty: this.dirty(),
        saving: this.saving(),
        onSave: () => void this.save(),
        onCancel: () => this.draft.set(entry.template),
      },
      this.body(entry),
    );
  }

  private body(entry: PromptCatalogEntry) {
    const saving = this.saving();
    const error = this.error();
    const previewError = this.previewError();
    const preview = this.preview();
    return html`
      <p class="prompt-editor__description">${entry.description}</p>

      ${entry.warnings.map(
        (warning) =>
          html`<p class="prompt-editor__warning" role="alert" data-role="prompt-warning">
            ${icon('warning')} ${warning}
          </p>`,
      )}

      <div class="prompt-editor__tokens" role="group" aria-label="Available tokens">
        <span class="prompt-editor__label">Available tokens — click to insert:</span>
        ${entry.tokens.map(
          (token) =>
            html`<button
              type="button"
              class="prompt-editor__token"
              data-token=${token}
              ?disabled=${saving}
              @click=${() => void this.insert(token)}
            >
              ${tokenLiteral(token)}
            </button>`,
        )}
      </div>

      ${field(
        { id: 'prompt-template', label: 'Prompt template', cls: 'prompt-editor__field' },
        (a) =>
          html`<textarea
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            class="prompt-editor__template"
            data-field="template"
            spellcheck="false"
            ?disabled=${saving}
            .value=${this.draft()}
            @input=${(e: Event) => this.draft.set((e.target as HTMLTextAreaElement).value)}
          ></textarea>`,
      )}
      ${
        entry.tokens.includes('narrator_identity')
          ? html`<p class="prompt-editor__note">
              <code>${tokenLiteral('narrator_identity')}</code> renders empty unless the book has a
              narrator link.
            </p>`
          : nothing
      }
      ${
        entry.tokens.includes('also_narrates')
          ? html`<p class="prompt-editor__note">
              <code>${tokenLiteral('also_narrates')}</code> renders empty unless this character has
              the book's narrator link.
            </p>`
          : nothing
      }
      ${
        entry.expectedResponse
          ? html`<section class="prompt-editor__expected">
              <span class="prompt-editor__label">
                Expected response (JSON) — displayed for reference, not editable:
              </span>
              <!-- Bound as a property: whitespace a formatter adds inside a <pre> would show. -->
              <pre
                class="prompt-editor__pre"
                data-role="expected-response"
                .textContent=${entry.expectedResponse}
              ></pre>
            </section>`
          : nothing
      }

      <details
        class="r2m-expansion prompt-editor__preview"
        ?open=${this.previewOpen()}
        @toggle=${(e: Event) => this.previewOpen.set((e.target as HTMLDetailsElement).open)}
      >
        <summary><span class="r2m-expansion__title">Preview with sample values</span></summary>
        <div class="r2m-expansion__body">
          ${
            previewError
              ? html`<p class="prompt-editor__error" role="alert">
                  The preview failed: ${previewError}
                </p>`
              : preview === null
                ? spinner(20)
                : html`<pre
                    class="prompt-editor__pre"
                    data-role="preview"
                    .textContent=${preview}
                  ></pre>`
          }
        </div>
      </details>

      <div class="prompt-editor__actions">
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="reset-prompt"
          ?disabled=${!this.resettable() || saving}
          @click=${() => void this.reset()}
        >
          ${icon('restart_alt')} Reset to default
        </button>
        <span class="prompt-editor__note">
          ${entry.isOverridden ? 'A stored override is in use.' : 'The built-in default is in use.'}
        </span>
      </div>

      ${error ? html`<p class="prompt-editor__error" role="alert">${error}</p>` : nothing}
    `;
  }

  private textarea(): HTMLTextAreaElement | null {
    return this.querySelector<HTMLTextAreaElement>('[data-field="template"]');
  }

  private async insert(token: string): Promise<void> {
    const element = this.textarea();
    const start = element ? element.selectionStart : null;
    const end = element ? element.selectionEnd : null;
    const { text, caret } = insertToken(this.draft(), token, start, end);
    this.draft.set(text);
    if (!element) return;
    // The binding writes the value on the next render; place the caret after it.
    await this.rendered();
    element.value = text;
    element.setSelectionRange(caret, caret);
    element.focus();
  }

  private async save(): Promise<void> {
    const entry = this.#entry();
    if (!entry) return;
    this.saving.set(true);
    this.error.set(null);
    try {
      await this.store.save(entry.kind, this.draft());
      this.toast.success(`${entry.title} saved`);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }

  private async reset(): Promise<void> {
    const entry = this.#entry();
    if (!entry) return;
    const message = entry.isOverridden
      ? 'The stored override and any unsaved edits are discarded; the built-in default is used again.'
      : 'Your unsaved edits are discarded.';
    const ok = await this.confirm.confirm({
      title: `Reset ${entry.title} to default?`,
      message,
      confirmLabel: 'Reset',
    });
    if (!ok) return;
    if (!entry.isOverridden) {
      this.draft.set(entry.defaultTemplate);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    try {
      await this.store.reset(entry.kind);
      this.draft.set(entry.defaultTemplate);
      this.toast.info(`${entry.title} reset to default`);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }

  /** The last rendering stays on screen while the next one is on its way — no spinner per keystroke. */
  private schedulePreview(open: boolean, kind: PromptCatalogEntry['kind'], template: string): void {
    this.clearPreviewTimer();
    if (!open) return;
    this.previewError.set(null);
    const seq = ++this.previewSeq;
    this.previewTimer = setTimeout(() => {
      this.previewTimer = null;
      void this.fetchPreview(seq, kind, template);
    }, PREVIEW_DEBOUNCE_MS);
  }

  private async fetchPreview(
    seq: number,
    kind: PromptCatalogEntry['kind'],
    template: string,
  ): Promise<void> {
    try {
      const rendered = await this.store.preview(kind, template);
      if (seq === this.previewSeq) this.preview.set(rendered);
    } catch (e) {
      if (seq === this.previewSeq) this.previewError.set(toApiError(e).message);
    }
  }

  private clearPreviewTimer(): void {
    if (this.previewTimer !== null) clearTimeout(this.previewTimer);
    this.previewTimer = null;
  }
}
define('r2m-prompt-editor', PromptEditor);
