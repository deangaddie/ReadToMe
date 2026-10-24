import { html, nothing } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import {
  type PronunciationDto,
  type PronunciationImpactDto,
  PronunciationsApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { adviceLine, blockable, icon } from '@app/ui/partials';
import {
  countLine,
  deleteBlockedReason,
  discardAdvice,
  duplicateWarning,
  isDirty,
  overlapNotes,
  saveBlockedReason,
} from './pronunciation-logic';
import { PronunciationWriter } from './pronunciation-writer';
import css from './pronunciation-editor.css' with { type: 'text' };
import './pronunciation-try';

adoptStyles(css);

/** How long typing must pause before the editor asks the host what the draft would do. */
export const IMPACT_DEBOUNCE_MS = 300;

/**
 * One Pronunciation's editor, shared by the Pronunciations page and (later) the Book view's panel:
 * written → spoken inputs, the draft's reach, overlap and duplicate notes from a debounced impact
 * call, Try it (`r2m-pronunciation-try`) over its matches, Save (Add for a new entry) off until
 * something changed or while a duplicate stands, the advice line saying what Save discards, Cancel,
 * and Delete behind its confirm. Writes go through {@link PronunciationWriter}; emits `editor-close`
 * after a save, a delete or Cancel.
 */
export class PronunciationEditor extends R2mElement {
  private readonly api = use(PronunciationsApi);
  private readonly writer = use(PronunciationWriter);

  readonly #folder = signal<string | null>(null);
  get folder() {
    return this.#folder();
  }
  set folder(value: string | null) {
    this.#folder.set(value);
  }

  /**
   * The entry being edited, or null for a new one. Another entry starts a fresh draft from it; the
   * same entry refetched (the list reloads on every receipt) only refreshes what the draft is
   * compared against and asks the impact again, so a draft survives another tab's save and its
   * counts follow it.
   */
  readonly #entry = signal<PronunciationDto | null>(null);
  get entry() {
    return this.#entry();
  }
  set entry(value: PronunciationDto | null) {
    const same = value !== null && value.id === this.#entry()?.id;
    this.#entry.set(value);
    if (same) {
      this.scheduleImpact();
      return;
    }
    this.written.set(value?.writtenForm ?? '');
    this.spoken.set(value?.spokenForm ?? '');
    this.impact.set(null);
    this.invalid.set(null);
    this.scheduleImpact();
  }

  /** `StepView.readOnly`: why nothing can change while a step is viewed, or null. */
  readonly #readOnly = signal<string | null>(null);
  get readOnly() {
    return this.#readOnly();
  }
  set readOnly(value: string | null) {
    this.#readOnly.set(value);
  }

  /** Items queued or processing for audio in this project: Save and Delete wait for them. */
  readonly #audioInFlight = signal(0);
  get audioInFlight() {
    return this.#audioInFlight();
  }
  set audioInFlight(value: number) {
    this.#audioInFlight.set(value);
  }

  readonly written = signal('');
  readonly spoken = signal('');
  /** The host's answer for the draft as last asked; kept while a newer question is pending. */
  readonly impact = signal<PronunciationImpactDto | null>(null);
  /** The host's 400 detail for the draft, or null. */
  readonly invalid = signal<string | null>(null);
  readonly writing = signal(false);

  readonly dirty = computed(() =>
    isDirty(this.#entry(), {
      writtenForm: this.written(),
      spokenForm: this.spoken(),
    }),
  );

  private readonly saveReason = computed(() =>
    saveBlockedReason({
      readOnly: this.#readOnly(),
      audioInFlight: this.#audioInFlight(),
      duplicateOf: this.impact()?.duplicateOf ?? null,
      invalid: this.invalid(),
    }),
  );

  private readonly deleteReason = computed(() =>
    deleteBlockedReason({
      readOnly: this.#readOnly(),
      audioInFlight: this.#audioInFlight(),
    }),
  );

  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Bumped per impact question, so an answer overtaken by a newer one lands nowhere. */
  private asked = 0;

  protected override connected(): void {
    this.classList.add('pron-editor');
    this.onDisconnect(() => this.cancelImpact());
  }

  private cancelImpact(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.asked++;
  }

  private scheduleImpact(): void {
    this.cancelImpact();
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.askImpact();
    }, IMPACT_DEBOUNCE_MS);
  }

  /** Asks what the draft would do; nothing while a step is viewed (the impact reads today's book). */
  private async askImpact(): Promise<void> {
    const folder = this.#folder();
    if (folder === null || this.#readOnly() !== null) return;
    if (this.written().trim() === '') {
      this.impact.set(null);
      this.invalid.set(null);
      return;
    }
    const asked = ++this.asked;
    try {
      const answer = await this.api.impact(folder, {
        id: this.#entry()?.id ?? null,
        writtenForm: this.written(),
        spokenForm: this.spoken(),
        delete: false,
      });
      if (asked !== this.asked) return;
      this.impact.set(answer);
      this.invalid.set(null);
    } catch (e) {
      if (asked !== this.asked) return;
      const error = toApiError(e);
      this.impact.set(null);
      this.invalid.set(error.status === 400 ? (error.detail ?? error.message) : null);
    }
  }

  private edit(field: 'written' | 'spoken', event: Event): void {
    (field === 'written' ? this.written : this.spoken).set(
      (event.target as HTMLInputElement).value,
    );
    this.scheduleImpact();
  }

  private async save(): Promise<void> {
    const folder = this.#folder();
    if (folder === null) return;
    this.writing.set(true);
    try {
      const saved = await this.writer.save(folder, this.#entry(), {
        writtenForm: this.written(),
        spokenForm: this.spoken(),
      });
      if (saved) this.close();
    } finally {
      this.writing.set(false);
    }
  }

  private async delete(): Promise<void> {
    const folder = this.#folder();
    const entry = this.#entry();
    if (folder === null || entry === null) return;
    this.writing.set(true);
    try {
      if (await this.writer.remove(folder, entry)) this.close();
    } finally {
      this.writing.set(false);
    }
  }

  private close(): void {
    this.cancelImpact();
    this.emit('editor-close');
  }

  protected template() {
    const entry = this.#entry();
    const readOnly = this.#readOnly();
    const impact = this.impact();
    const dirty = this.dirty();
    const complete = this.written().trim() !== '' && this.spoken().trim() !== '';
    const writing = this.writing();
    const saveReason = this.saveReason();
    const deleteReason = this.deleteReason();
    // Until the host answers, an unchanged entry's own reach stands in.
    const reach = impact ?? (entry && !dirty ? entry : null);
    const duplicate = duplicateWarning(impact?.duplicateOf ?? null);
    const overlaps = overlapNotes(impact?.overlaps ?? []);
    const invalid = this.invalid();
    return html`
      <header class="pron-editor__head">
        <h2 class="pron-editor__title">${entry ? entry.writtenForm : 'New pronunciation'}</h2>
        <button
          type="button"
          class="r2m-icon-button"
          data-action="close"
          aria-label="Close"
          data-tooltip="Close"
          @click=${() => this.close()}
        >
          ${icon('close')}
        </button>
      </header>
      <div class="pron-editor__fields">
        <label class="r2m-field">
          <span class="r2m-field__label">Written form</span>
          ${blockable(
            readOnly,
            html`<input
              data-field="written"
              maxlength="100"
              placeholder="Kulgan"
              .value=${live(this.written())}
              ?disabled=${readOnly !== null}
              @input=${(e: Event) => this.edit('written', e)}
            />`,
          )}
        </label>
        <span class="pron-editor__arrow" aria-hidden="true">${icon('arrow_forward')}</span>
        <label class="r2m-field">
          <span class="r2m-field__label">Spoken form</span>
          ${blockable(
            readOnly,
            html`<input
              data-field="spoken"
              maxlength="200"
              placeholder="KULL-gun"
              .value=${live(this.spoken())}
              ?disabled=${readOnly !== null}
              @input=${(e: Event) => this.edit('spoken', e)}
            />`,
          )}
        </label>
      </div>
      ${
        reach
          ? html`<p
              class="pron-editor__count ${reach.matchCount === 0 ? 'pron-editor__count--none' : ''}"
              data-role="count"
            >
              ${countLine(reach)}
            </p>`
          : nothing
      }
      ${
        invalid !== null
          ? html`<p class="pron-editor__note pron-editor__note--warn" data-role="invalid">
              ${icon('warning')} ${invalid}
            </p>`
          : nothing
      }
      ${
        duplicate !== null
          ? html`<p class="pron-editor__note pron-editor__note--warn" data-role="duplicate">
              ${icon('warning')} ${duplicate}
            </p>`
          : nothing
      }
      ${
        overlaps.length > 0
          ? html`<p class="pron-editor__note" data-role="overlap">
              ${icon('info')} ${overlaps.join(' ')}
            </p>`
          : nothing
      }
      ${
        impact && impact.matches.length > 0
          ? html`<r2m-pronunciation-try
              .folder=${this.#folder()}
              .matchesInBook=${impact.matches}
              .draft=${{
                id: entry?.id ?? null,
                writtenForm: this.written(),
                spokenForm: this.spoken(),
                delete: false,
              }}
              .readOnly=${readOnly}
            ></r2m-pronunciation-try>`
          : nothing
      }
      <div class="pron-editor__actions">
        ${
          entry
            ? blockable(
                deleteReason,
                html`<button
                  type="button"
                  class="r2m-button pron-editor__delete"
                  data-action="delete"
                  ?disabled=${deleteReason !== null || writing}
                  @click=${() => void this.delete()}
                >
                  ${icon('delete')} Delete
                </button>`,
              )
            : nothing
        }
        <span class="pron-editor__spacer"></span>
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="cancel"
          @click=${() => this.close()}
        >
          Cancel
        </button>
        ${blockable(
          saveReason,
          html`<button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="save"
            ?disabled=${saveReason !== null || !dirty || !complete || writing}
            @click=${() => void this.save()}
          >
            ${icon('save')} ${entry ? 'Save' : 'Add'}
          </button>`,
        )}
      </div>
      ${readOnly === null && dirty ? adviceLine(discardAdvice(impact?.audioDiscardCount ?? 0)) : nothing}
    `;
  }
}
define('r2m-pronunciation-editor', PronunciationEditor);
