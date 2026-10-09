import { html, nothing } from 'lit-html';
import { type VoiceDto, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { icon } from '@app/ui/partials';
import type { SettingsSchema, SettingsValues } from '@app/ui/settings-form';
import { tabPanel, tabs } from '@app/ui/tabs';
import { PROVIDER_AREA_LABEL, type ProviderArea, ProviderSchemas } from './provider-schemas';
import { overrideDirty, overrideJson, overrideValues } from './voice-logic';
import '@app/ui/settings-form';

/** Which override column an editor tab edits, and the command that saves it. */
export interface OverrideSave {
  area: ProviderArea;
  /** Null clears the override. */
  json: string | null;
}

/**
 * One tab of the Advanced settings: an `r2m-settings-form` in override mode over the active
 * provider's schema, seeded from the voice's stored patch. Save is dirty-gated and blocked while a
 * field is invalid. Emits `save` with the sparse patch as the column value; the parent posts the
 * command.
 */
export class VoiceOverrideEditor extends R2mElement {
  private readonly schemas = use(ProviderSchemas);

  readonly #area = signal<ProviderArea>('voice-design');
  get area() {
    return this.#area();
  }
  set area(value: ProviderArea) {
    this.#area.set(value);
  }

  /** The stored override column (null = no override). */
  readonly #storedJson = signal<string | null>(null);
  get storedJson() {
    return this.#storedJson();
  }
  set storedJson(value: string | null) {
    this.#storedJson.set(value);
  }

  readonly #disabled = signal(false);
  get disabled() {
    return this.#disabled();
  }
  set disabled(value: boolean) {
    this.#disabled.set(value);
  }

  readonly schema = signal<SettingsSchema | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  /** The stored column as form values; the form re-seeds from it whenever the voice reloads. */
  readonly stored = computed(() => overrideValues(this.#storedJson()));
  readonly patch = signal<SettingsValues>({});
  readonly valid = signal(true);
  readonly dirty = computed(() => overrideDirty(this.#storedJson(), this.patch()));
  readonly json = computed(() => overrideJson(this.patch()));

  protected override connected(): void {
    this.classList.add('voice-override');
    this.effect(() => {
      const area = this.#area();
      this.setAttribute('data-area', area);
      untracked(() => void this.load(area));
    });
    // A reload of the voice (save landed, another tab wrote) resets the working patch to it.
    this.effect(() => {
      const stored = this.stored();
      untracked(() => this.patch.set(stored));
    });
  }

  protected template() {
    const label = PROVIDER_AREA_LABEL[this.#area()];
    const error = this.error();
    const schema = this.schema();
    if (this.loading()) {
      return html`<progress max="1" aria-label="Loading provider settings"></progress>`;
    }
    if (error) return html`<p class="voice-override__note" role="alert">${error}</p>`;
    if (!schema) {
      return html`<p class="voice-override__note">
        No active ${label} provider — nothing to override.
      </p>`;
    }
    return html`
      <p class="voice-override__note">
        Overriding the active ${label} provider (${schema.type}). Fields left alone use the
        provider's settings.
      </p>
      <r2m-settings-form
        .mode=${'override'}
        .schema=${schema}
        .values=${this.stored()}
        .disabled=${this.#disabled()}
        @values-change=${(e: Event) => this.patch.set((e as CustomEvent<SettingsValues>).detail)}
        @valid-change=${(e: Event) => this.valid.set((e as CustomEvent<boolean>).detail)}
      ></r2m-settings-form>
      <button
        type="button"
        class="r2m-button r2m-button--stroked"
        data-action="save-override"
        ?disabled=${this.#disabled() || !this.dirty() || !this.valid()}
        @click=${() => this.emit<OverrideSave>('save', { area: this.#area(), json: this.json() })}
      >
        ${icon('save')} Save
      </button>
    `;
  }

  private async load(area: ProviderArea): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.schema.set(await this.schemas.active(area));
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }
}
define('r2m-voice-override-editor', VoiceOverrideEditor);

export type OverrideTab = ProviderArea;

const OVERRIDE_TABS: readonly { id: OverrideTab; label: string }[] = [
  { id: 'voice-design', label: 'Voice Design' },
  { id: 'paragraph-tts', label: 'Text-to-Speech' },
];

export interface VoiceOverridesOptions {
  voice: VoiceDto;
  disabled: boolean;
  /** Which override tab is open; the card owns it. */
  selected: OverrideTab;
  onSelect: (tab: OverrideTab) => void;
  onSave: (save: OverrideSave) => void;
}

/** "Advanced settings": the Voice Design and Text-to-Speech override tabs over `tabs()`. */
export function voiceOverrides(options: VoiceOverridesOptions) {
  const { voice, selected } = options;
  const id = `voice-overrides-${voice.id}`;
  const storedJson =
    selected === 'voice-design'
      ? voice.voiceDesignSettingsOverrideJson
      : voice.ttsSettingsOverrideJson;
  return html`
    ${tabs({
      id,
      tabs: OVERRIDE_TABS,
      selected,
      onSelect: options.onSelect,
      label: 'Advanced settings',
    })}
    ${tabPanel(
      id,
      selected,
      html`<r2m-voice-override-editor
        .area=${selected}
        .storedJson=${storedJson ?? null}
        .disabled=${options.disabled}
        @save=${(e: Event) => options.onSave((e as CustomEvent<OverrideSave>).detail)}
      ></r2m-voice-override-editor>`,
    )}
    ${nothing}
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-override-editor': VoiceOverrideEditor;
  }
}
