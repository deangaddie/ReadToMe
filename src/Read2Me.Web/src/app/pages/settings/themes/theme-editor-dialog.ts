import { html, nothing } from 'lit-html';
import { styleMap } from 'lit-html/directives/style-map.js';
import type { AppTheme } from '@app/api';
import { type ClosableContent, openDialog, requestDialogClose } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { contrastText, isHexColour, normalizeHex } from '@app/theme/theme-css';
import { ConfirmService } from '@app/ui/dialogs';
import { controlValue, field } from '@app/ui/field';
import { icon } from '@app/ui/partials';
import {
  DEFAULT_DRAFT,
  OPTIONAL_COLOURS,
  type OptionalColourKey,
  type ThemeDraft,
  type ThemeDraftErrors,
  fromDraft,
  sameDraft,
  toDraft,
  validateDraft,
} from './theme-form';
import css from './theme-editor-dialog.css' with { type: 'text' };

adoptStyles(css);

export interface ThemeEditorData {
  /** Existing theme to edit; omitted for New. Built-in themes open as a copy. */
  theme?: AppTheme;
}

export type ThemeEditorResult = Omit<AppTheme, 'id' | 'isBuiltIn'>;

type ColourKey = 'primary' | 'secondary' | OptionalColourKey;

/** The fields in reading order: a failed submit focuses the first one with an error. */
const FIELD_ORDER: readonly (keyof ThemeDraftErrors)[] = [
  'name',
  'primary',
  'secondary',
  ...OPTIONAL_COLOURS.map(({ key }) => key),
];

/**
 * Theme create/edit form (design §6.4 dialog variant): name, primary/secondary via native colour
 * inputs with a hex readout, dark switch, clearable advanced colours and a live preview strip.
 * Closes with the API body on Save / Create, with nothing on Cancel. Cancel, Escape and a backdrop
 * click ask before a changed draft is thrown away.
 */
export class ThemeEditorDialog extends R2mElement implements ClosableContent {
  private readonly confirm = use(ConfirmService);
  #data: ThemeEditorData = {};
  /** The draft as it opened: Cancel discards nothing while the draft still equals it. */
  #opened: ThemeDraft = { ...DEFAULT_DRAFT };
  get data() {
    return this.#data;
  }
  set data(value: ThemeEditorData) {
    this.#data = value;
    const theme = value.theme;
    this.#opened = theme
      ? { ...toDraft(theme), name: theme.isBuiltIn ? `${theme.name} copy` : theme.name }
      : { ...DEFAULT_DRAFT };
    this.draft.set({ ...this.#opened });
    this.touched.set({});
    this.showAdvanced.set(!!theme && OPTIONAL_COLOURS.some(({ key }) => !!theme[key]));
  }

  readonly draft = signal<ThemeDraft>({ ...DEFAULT_DRAFT });
  readonly touched = signal<Partial<Record<keyof ThemeDraft, boolean>>>({});
  readonly errors = computed<ThemeDraftErrors>(() => validateDraft(this.draft()));
  readonly valid = computed(() => Object.keys(this.errors()).length === 0);
  readonly showAdvanced = signal(false);

  /** Preview strip colours: fall back to plausible defaults for blank optionals. */
  readonly preview = computed(() => {
    const d = this.draft();
    const safe = (v: string, fallback: string) => (isHexColour(v) ? normalizeHex(v) : fallback);
    const background = safe(d.background, d.isDark ? '#141518' : '#faf9fd');
    const surface = safe(d.surface, d.isDark ? '#202226' : '#f1f0f4');
    const primary = safe(d.primary, '#594ae2');
    const secondary = safe(d.secondary, '#ff4081');
    const appbar = safe(d.appbarBackground, surface);
    const nav = safe(d.drawerBackground, surface);
    const text = safe(d.textPrimary, contrastText(background));
    return {
      background,
      surface,
      primary,
      secondary,
      appbar,
      appbarText: contrastText(appbar),
      nav,
      navText: contrastText(nav),
      text,
      onPrimary: contrastText(primary),
    };
  });

  get isEdit(): boolean {
    return !!this.#data.theme && !this.#data.theme.isBuiltIn;
  }

  get heading(): string {
    const theme = this.#data.theme;
    return this.isEdit ? `Edit ${theme?.name}` : theme ? `Copy of ${theme.name}` : 'New theme';
  }

  error(key: keyof ThemeDraft): string | null {
    return this.touched()[key] ? (this.errors()[key] ?? null) : null;
  }

  protected template() {
    const draft = this.draft();
    const p = this.preview();
    const showAdvanced = this.showAdvanced();
    return html`
      <h2 class="r2m-dialog__title">${this.heading}</h2>

      <form novalidate @submit=${(e: SubmitEvent) => this.onSubmit(e)}>
        <div class="r2m-dialog__content editor">
          <div
            class="editor__preview"
            style=${styleMap({ background: p.background })}
            aria-hidden="true"
          >
            <div
              class="editor__preview-bar"
              style=${styleMap({ background: p.appbar, color: p.appbarText })}
            >
              Read2Me
            </div>
            <div class="editor__preview-body">
              <div
                class="editor__preview-nav"
                style=${styleMap({ background: p.nav, color: p.navText })}
              >
                ▤
              </div>
              <div class="editor__preview-card" style=${styleMap({ background: p.surface })}>
                <span style=${styleMap({ color: p.text })}>Aa</span>
                <span
                  class="editor__preview-chip"
                  style=${styleMap({ background: p.primary, color: p.onPrimary })}
                  >Primary</span
                >
                <span
                  class="editor__preview-dot"
                  style=${styleMap({ background: p.secondary })}
                ></span>
              </div>
            </div>
          </div>

          ${field(
            { id: 'theme-name', label: 'Name', error: this.error('name'), cls: 'editor__name' },
            (a) =>
              html`<input
                id=${a.id}
                aria-describedby=${a.describedBy}
                aria-invalid=${a.invalid}
                type="text"
                name="name"
                autofocus
                required
                maxlength="60"
                autocomplete="off"
                .value=${draft.name}
                @input=${(e: Event) => this.setName(controlValue(e))}
                @blur=${() => this.touch('name')}
              />`,
          )}

          <div class="editor__row">
            ${this.colour('primary', 'Primary', draft.primary, this.error('primary'))}
            ${this.colour('secondary', 'Secondary', draft.secondary, this.error('secondary'))}
            <label class="r2m-switch">
              <input
                type="checkbox"
                role="switch"
                name="isDark"
                .checked=${draft.isDark}
                @change=${(e: Event) => this.setDark((e.target as HTMLInputElement).checked)}
              />
              Dark mode
            </label>
          </div>

          <button
            type="button"
            class="r2m-button editor__advanced-toggle"
            data-action="toggle-advanced"
            aria-expanded=${showAdvanced ? 'true' : 'false'}
            @click=${() => this.showAdvanced.update((v) => !v)}
          >
            ${icon(showAdvanced ? 'expand_less' : 'expand_more')} Advanced colours
          </button>

          ${
            showAdvanced
              ? html`<div class="editor__advanced" data-role="advanced">
                  ${OPTIONAL_COLOURS.map((opt) =>
                    this.colour(opt.key, opt.label, draft[opt.key], this.error(opt.key), opt.hint),
                  )}
                </div>`
              : nothing
          }
        </div>

        <div class="r2m-dialog__actions">
          <button
            type="button"
            class="r2m-button editor__cancel"
            @click=${() => requestDialogClose(this)}
          >
            Cancel
          </button>
          <button
            type="submit"
            class="r2m-button r2m-button--filled editor__save"
            data-action="save-theme"
          >
            ${this.isEdit ? 'Save' : 'Create'}
          </button>
        </div>
      </form>
    `;
  }

  /** A colour row: the native picker, the hex readout, and for optionals a Clear and a hint. */
  private colour(
    key: ColourKey,
    label: string,
    value: string,
    error: string | null,
    hint?: string,
  ) {
    const optional = hint !== undefined;
    const errorId = error ? `theme-${key}-error` : null;
    const hintId = !errorId && hint ? `theme-${key}-hint` : null;
    return html`<div
      class="editor__colour ${optional ? 'editor__colour--optional' : ''}"
      data-colour=${key}
    >
      <span class="editor__colour-label">${label}</span>
      <input
        type="color"
        .value=${this.pickerValue(key)}
        aria-label="${label} colour"
        @input=${(e: Event) => this.setColour(key, controlValue(e))}
        @change=${() => this.touch(key)}
      />
      <input
        class="editor__hex"
        type="text"
        spellcheck="false"
        placeholder=${optional ? 'default' : nothing}
        aria-label="${label} hex"
        aria-invalid=${errorId ? 'true' : nothing}
        aria-describedby=${errorId ?? hintId ?? nothing}
        .value=${value}
        @input=${(e: Event) => this.setColour(key, controlValue(e))}
        @blur=${() => this.touch(key)}
      />
      ${
        optional
          ? html`<button
              type="button"
              class="r2m-icon-button editor__clear"
              ?disabled=${!value}
              aria-label="Clear ${label}"
              data-tooltip="Clear ${label}"
              @click=${() => this.setColour(key, '')}
            >
              ${icon('backspace')}
            </button>`
          : nothing
      }
      ${
        errorId
          ? html`<span class="editor__error" id=${errorId}>${error}</span>`
          : hintId
            ? html`<span class="editor__hint" id=${hintId}>${hint}</span>`
            : nothing
      }
    </div>`;
  }

  setName(value: string): void {
    this.draft.update((d) => ({ ...d, name: value }));
  }

  setDark(value: boolean): void {
    this.draft.update((d) => ({ ...d, isDark: value }));
  }

  setColour(key: ColourKey, value: string): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }

  /** Native colour inputs only accept #rrggbb; give them a valid value or a neutral fallback. */
  pickerValue(key: ColourKey): string {
    const v = this.draft()[key];
    return isHexColour(v) ? normalizeHex(v) : '#888888';
  }

  async canClose(): Promise<boolean> {
    if (sameDraft(this.draft(), this.#opened)) return true;
    return this.confirm.confirm({
      title: 'Discard changes?',
      message: 'The changes to this theme have not been saved.',
      confirmLabel: 'Discard',
      destructive: true,
    });
  }

  onSubmit(e: SubmitEvent): void {
    e.preventDefault();
    void this.save();
  }

  /**
   * Submit: every field touched. An invalid draft focuses its first invalid field, opening the
   * advanced colours when that one is hidden behind the toggle, and closes with nothing.
   */
  async save(): Promise<void> {
    this.touched.set(Object.fromEntries(FIELD_ORDER.map((k) => [k, true])));
    const errors = this.errors();
    const first = FIELD_ORDER.find((k) => errors[k]);
    if (!first) {
      this.emit('r2m-close', fromDraft(this.draft()));
      return;
    }
    if (OPTIONAL_COLOURS.some(({ key }) => key === first)) this.showAdvanced.set(true);
    await this.rendered();
    const control =
      first === 'name'
        ? this.querySelector<HTMLElement>('input[name="name"]')
        : this.querySelector<HTMLElement>(`[data-colour="${first}"] .editor__hex`);
    control?.focus();
  }

  private touch(key: keyof ThemeDraft): void {
    this.touched.update((t) => ({ ...t, [key]: true }));
  }
}
define('r2m-theme-editor-dialog', ThemeEditorDialog);

/** Opens the editor; resolves the API body to create or update, or null when dismissed. */
export async function openThemeEditorDialog(
  data: ThemeEditorData,
): Promise<ThemeEditorResult | null> {
  const dialog = document.createElement('r2m-theme-editor-dialog');
  dialog.data = data;
  return (await openDialog<ThemeEditorResult>(dialog)) ?? null;
}

/** The editor as a service, so a spec can `override()` it: happy-dom cannot open a modal dialog. */
export class ThemeEditor {
  open(data: ThemeEditorData): Promise<ThemeEditorResult | null> {
    return openThemeEditorDialog(data);
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-theme-editor-dialog': ThemeEditorDialog;
  }
}
