import { html, nothing } from 'lit-html';
import { toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import type { HasUnsavedChanges } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { confirmDiscard } from '@app/shared/unsaved-changes';
import type { ConfigListItem } from '@app/ui/config-list';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  PARAGRAPH_TTS_AREA,
  PROVIDER_PAGE_TAGS,
  type ProviderArea,
  SIMILARITY_AREA,
  TRANSCRIPTION_AREA,
  VOICE_DESIGN_AREA,
  providerType,
} from './provider-area';
import type { ProviderConfigEditor, ProviderEditTarget } from './provider-config-editor';
import {
  type ProviderConfig,
  duplicateProviderForm,
  emptyProviderForm,
  sameProviderForm,
  toProviderForm,
} from './provider-form';
import { ProviderSettingsStore } from './provider-settings-store';
import type { VoiceDesignSampleTextCard } from './voice-design-sample-text';
import css from './provider-settings-page.css' with { type: 'text' };
import '@app/ui/config-list';
import './provider-config-editor';
import './provider-test-panels';
import './voice-design-sample-text';

adoptStyles(css);

/**
 * `/settings/tts`, `/settings/voice-design`, `/settings/transcription` and `/settings/similarity`
 * (native-web 35): the settings master–detail template over one provider area's configs — list with
 * Make active / Duplicate / Delete on the left, the typed in-place editor on the right — then the
 * area's Test action for the selected saved config. Voice design leads with its sample text. One
 * element per area (the outlet keeps an element whose tag matches, so a shared tag would not
 * change area on navigation); each provides its {@link ProviderSettingsStore} to its children,
 * and the router's `guardUnsaved` asks {@link hasUnsavedChanges} before leaving.
 */
export abstract class ProviderSettingsPage extends R2mElement implements HasUnsavedChanges {
  readonly store: ProviderSettingsStore;
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);

  /** Set on select / new / duplicate / save — never recomputed under an edit in progress. */
  readonly target = signal<ProviderEditTarget | null>(null);
  readonly busy = signal(false);

  readonly selectedId = computed(() => {
    const id = this.target()?.id;
    return id ? String(id) : null;
  });

  /** The saved config in the editor — what a Test action runs against. */
  readonly selected = computed(() => {
    const id = this.target()?.id;
    return (id && this.store.configs()?.find((c) => c.id === id)) || null;
  });

  readonly items = computed<ConfigListItem[]>(() =>
    (this.store.configs() ?? []).map((c) => {
      const form = this.form(c);
      return {
        id: String(c.id),
        name: c.name,
        subtitle: [providerType(this.area, c.type).label, form.baseUrl, this.area.detail?.(form)]
          .filter(Boolean)
          .join(' · '),
        isActive: c.id === this.store.activeId(),
      };
    }),
  );

  protected constructor(readonly area: ProviderArea) {
    super();
    this.store = provide(this, ProviderSettingsStore, new ProviderSettingsStore(area));
  }

  protected override connected(): void {
    void this.store.open();
    // Keeps the editor on a config that still exists and, while it holds no edits, on its latest
    // saved state — a reload may carry a change made in another tab or over the API.
    this.effect(() => {
      const configs = this.store.configs();
      if (!configs) return;
      untracked(() => this.reconcile(configs));
    });
    this.onDisconnect(() => this.store.close());
  }

  hasUnsavedChanges(): boolean {
    return this.editorDirty() || (this.sampleText()?.dirty() ?? false);
  }

  private editorDirty(): boolean {
    return this.querySelector<ProviderConfigEditor>('r2m-provider-config-editor')?.dirty() ?? false;
  }

  private sampleText(): VoiceDesignSampleTextCard | null {
    return this.querySelector<VoiceDesignSampleTextCard>('r2m-voice-design-sample-text');
  }

  protected template() {
    const area = this.area;
    const error = this.store.error();
    const configs = this.store.configs();
    const target = this.target();
    const selected = this.selected();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">${area.title}</h1>
        <p class="r2m-page-header__subtitle">${area.subtitle}</p>
      </header>

      ${
        error
          ? statusChip({
              status: 'error',
              label: `${area.title} settings could not be loaded: ${error}`,
            })
          : nothing
      }
      ${
        area.key === 'voice-design'
          ? html`<r2m-voice-design-sample-text></r2m-voice-design-sample-text>`
          : nothing
      }
      ${
        configs
          ? html`
              <div class="r2m-master-detail">
                <r2m-config-list
                  class="providers__list"
                  .items=${this.items()}
                  .selectedId=${this.selectedId()}
                  .busy=${this.busy()}
                  @pick=${(e: CustomEvent<string>) => void this.select(+e.detail)}
                  @create=${() => void this.create()}
                  @make-active=${(e: CustomEvent<string>) => void this.makeActive(+e.detail)}
                  @duplicate=${(e: CustomEvent<string>) => void this.duplicate(+e.detail)}
                  @delete=${(e: CustomEvent<string>) => void this.delete(+e.detail)}
                ></r2m-config-list>
                ${
                  target
                    ? html`<r2m-provider-config-editor
                        class="providers__editor"
                        .area=${area}
                        .target=${target}
                        @saved=${(e: CustomEvent<ProviderConfig>) => this.open(e.detail)}
                      ></r2m-provider-config-editor>`
                    : html`<div class="providers__editor">
                        ${emptyState({
                          icon: area.icon,
                          headline: configs.length
                            ? 'Select a configuration'
                            : `No ${area.noun} configurations yet`,
                          hint: configs.length
                            ? 'Or create a new one.'
                            : 'Create one to get started.',
                        })}
                      </div>`
                }
              </div>
              ${selected ? this.testPanel(selected) : nothing}
            `
          : nothing
      }
    `;
  }

  private testPanel(config: ProviderConfig) {
    switch (this.area.key) {
      case 'voice-design':
        return html`<r2m-voice-design-test .config=${config}></r2m-voice-design-test>`;
      case 'transcription':
        return html`<r2m-transcription-test .config=${config}></r2m-transcription-test>`;
      case 'semantic-similarity':
        return html`<r2m-similarity-test .config=${config}></r2m-similarity-test>`;
      default:
        return nothing;
    }
  }

  private form(config: ProviderConfig) {
    return toProviderForm(config, this.area.hasTextProcessing);
  }

  private reconcile(configs: ProviderConfig[]): void {
    const target = this.target();
    if (!target) {
      const first = configs.find((c) => c.id === this.store.activeId()) ?? configs[0];
      if (first) this.open(first);
      return;
    }
    if (!target.id) return;
    const current = configs.find((c) => c.id === target.id);
    if (!current) this.target.set(null);
    else if (!this.editorDirty() && !sameProviderForm(this.form(current), target.baseline))
      this.open(current);
  }

  private open(config: ProviderConfig): void {
    const form = this.form(config);
    this.target.set({ id: config.id, baseline: form, draft: form });
  }

  private find(id: number): ProviderConfig | undefined {
    return this.store.configs()?.find((c) => c.id === id);
  }

  private async mayLeaveEditor(): Promise<boolean> {
    return !this.editorDirty() || confirmDiscard(this.confirm);
  }

  private empty() {
    return emptyProviderForm(this.area.types[0]!.value, this.area.hasTextProcessing);
  }

  private async select(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || id === this.target()?.id || !(await this.mayLeaveEditor())) return;
    this.open(config);
  }

  private async create(): Promise<void> {
    if (!(await this.mayLeaveEditor())) return;
    const empty = this.empty();
    this.target.set({ id: 0, baseline: empty, draft: empty });
  }

  /** Opens an unsaved copy in the editor: nothing is stored until Save. */
  private async duplicate(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || !(await this.mayLeaveEditor())) return;
    const names = (this.store.configs() ?? []).map((c) => c.name);
    const draft = duplicateProviderForm(this.form(config), names, () => crypto.randomUUID());
    this.target.set({ id: 0, baseline: this.empty(), draft });
  }

  private makeActive(id: number): Promise<void> {
    return this.run(() => this.store.makeActive(id), 'Active configuration changed');
  }

  private async delete(id: number): Promise<void> {
    const config = this.find(id);
    if (!config) return;
    const consequence =
      id === this.store.activeId() ? 'Another configuration becomes the active one. ' : '';
    const ok = await this.confirm.confirm({
      title: `Delete ${config.name}?`,
      message: `${consequence}This cannot be undone.`,
      destructive: true,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    await this.run(() => this.store.delete(id), `Deleted ${config.name}`);
  }

  private async run(action: () => Promise<void>, success: string): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      this.toast.success(success);
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.busy.set(false);
    }
  }
}

export class TtsSettingsPage extends ProviderSettingsPage {
  constructor() {
    super(PARAGRAPH_TTS_AREA);
  }
}
define(PROVIDER_PAGE_TAGS['paragraph-tts'], TtsSettingsPage);

export class VoiceDesignSettingsPage extends ProviderSettingsPage {
  constructor() {
    super(VOICE_DESIGN_AREA);
  }
}
define(PROVIDER_PAGE_TAGS['voice-design'], VoiceDesignSettingsPage);

export class TranscriptionSettingsPage extends ProviderSettingsPage {
  constructor() {
    super(TRANSCRIPTION_AREA);
  }
}
define(PROVIDER_PAGE_TAGS.transcription, TranscriptionSettingsPage);

export class SimilaritySettingsPage extends ProviderSettingsPage {
  constructor() {
    super(SIMILARITY_AREA);
  }
}
define(PROVIDER_PAGE_TAGS['semantic-similarity'], SimilaritySettingsPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-tts-settings-page': TtsSettingsPage;
    'r2m-voice-design-settings-page': VoiceDesignSettingsPage;
    'r2m-transcription-settings-page': TranscriptionSettingsPage;
    'r2m-similarity-settings-page': SimilaritySettingsPage;
  }
}
