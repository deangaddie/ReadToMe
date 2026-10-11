import { html, nothing } from 'lit-html';
import { type LlmServerConfig, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import type { HasUnsavedChanges } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { duplicateName } from '@app/shared/config-form';
import { confirmDiscard } from '@app/shared/unsaved-changes';
import type { ConfigListItem } from '@app/ui/config-list';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { type LlmConfigEditor, type LlmEditTarget } from './llm-config-editor';
import { API_TYPE_LABELS, EMPTY_LLM_FORM, sameLlmForm, toLlmForm } from './llm-config-form';
import { LlmSettingsStore } from './llm-settings-store';
import css from './llm-settings-page.css' with { type: 'text' };
import '@app/ui/config-list';
import './llm-config-editor';
import './chain-card';
import './llm-test-console';

adoptStyles(css);

/**
 * `/settings/llm` (ticket 21): the settings master–detail template over LLM configs — list with
 * Make default / Duplicate / Delete on the left, the in-place editor on the right — then the
 * attribution escalation chain and the test console for the default config. Provides the
 * {@link LlmSettingsStore} to its children; the router's `guardUnsaved` asks
 * {@link hasUnsavedChanges} before leaving.
 */
export class LlmSettingsPage extends R2mElement implements HasUnsavedChanges {
  readonly store = provide(this, LlmSettingsStore, new LlmSettingsStore());
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);

  /** Set on select / new / duplicate / save — never recomputed under an edit in progress. */
  readonly target = signal<LlmEditTarget | null>(null);
  readonly busy = signal(false);

  readonly selectedId = computed(() => {
    const id = this.target()?.id;
    return id ? String(id) : null;
  });

  readonly items = computed<ConfigListItem[]>(() =>
    (this.store.configs() ?? []).map((c) => ({
      id: String(c.id),
      name: c.name,
      subtitle: [API_TYPE_LABELS[c.apiType], c.model, c.baseUrl].filter(Boolean).join(' · '),
      isActive: c.id === this.store.activeId(),
    })),
  );

  protected override connected(): void {
    this.classList.add('llm');
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
    return this.editor()?.dirty() ?? false;
  }

  private editor(): LlmConfigEditor | null {
    return this.querySelector<LlmConfigEditor>('r2m-llm-config-editor');
  }

  protected template() {
    const error = this.store.error();
    const configs = this.store.configs();
    const target = this.target();
    const active = this.store.active();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">LLM</h1>
        <p class="r2m-page-header__subtitle">
          Servers for attribution, voice prompts, AI book edits and character discovery.
        </p>
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `LLM settings could not be loaded: ${error}` })
          : nothing
      }
      ${
        configs
          ? html`
              <div class="r2m-master-detail">
                <r2m-config-list
                  class="llm__list"
                  .items=${this.items()}
                  .selectedId=${this.selectedId()}
                  .busy=${this.busy()}
                  .activeLabel=${'Default'}
                  @pick=${(e: CustomEvent<string>) => void this.select(+e.detail)}
                  @create=${() => void this.create()}
                  @make-active=${(e: CustomEvent<string>) => void this.makeDefault(+e.detail)}
                  @duplicate=${(e: CustomEvent<string>) => void this.duplicate(+e.detail)}
                  @delete=${(e: CustomEvent<string>) => void this.delete(+e.detail)}
                ></r2m-config-list>
                ${
                  target
                    ? html`<r2m-llm-config-editor
                        class="llm__editor"
                        .target=${target}
                        @saved=${(e: CustomEvent<LlmServerConfig>) => this.onSaved(e.detail)}
                      ></r2m-llm-config-editor>`
                    : html`<div class="llm__editor">
                        ${emptyState({
                          icon: 'smart_toy',
                          headline: configs.length
                            ? 'Select a configuration'
                            : 'No LLM configurations yet',
                          hint: configs.length
                            ? 'Or create a new one.'
                            : 'Create one to get started.',
                        })}
                      </div>`
                }
              </div>

              ${
                configs.length
                  ? html`
                      <p class="llm__note">
                        The <b>default</b> configuration is used for voice prompts, AI book edits
                        and character discovery. Character attribution uses the attribution chain
                        below instead.
                      </p>
                      <r2m-llm-chain-card></r2m-llm-chain-card>
                    `
                  : nothing
              }
              ${active ? html`<r2m-llm-test-console .config=${active}></r2m-llm-test-console>` : nothing}
            `
          : nothing
      }
    `;
  }

  private reconcile(configs: LlmServerConfig[]): void {
    const target = this.target();
    if (!target) {
      const first = configs.find((c) => c.id === this.store.activeId()) ?? configs[0];
      if (first) this.open(first);
      return;
    }
    if (!target.id) return;
    const current = configs.find((c) => c.id === target.id);
    if (!current) this.target.set(null);
    else if (!this.hasUnsavedChanges() && !sameLlmForm(toLlmForm(current), target.baseline))
      this.open(current);
  }

  private open(config: LlmServerConfig): void {
    const form = toLlmForm(config);
    this.target.set({ id: config.id, baseline: form, draft: form });
  }

  private find(id: number): LlmServerConfig | undefined {
    return this.store.configs()?.find((c) => c.id === id);
  }

  private async mayLeaveEditor(): Promise<boolean> {
    return !this.hasUnsavedChanges() || confirmDiscard(this.confirm);
  }

  private async select(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || id === this.target()?.id || !(await this.mayLeaveEditor())) return;
    this.open(config);
  }

  private async create(): Promise<void> {
    if (!(await this.mayLeaveEditor())) return;
    this.target.set({ id: 0, baseline: EMPTY_LLM_FORM, draft: EMPTY_LLM_FORM });
  }

  /** Opens an unsaved copy in the editor: nothing is stored until Save. */
  private async duplicate(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || !(await this.mayLeaveEditor())) return;
    const names = (this.store.configs() ?? []).map((c) => c.name);
    const draft = { ...toLlmForm(config), name: duplicateName(config.name, names) };
    this.target.set({ id: 0, baseline: EMPTY_LLM_FORM, draft });
  }

  private onSaved(config: LlmServerConfig): void {
    this.open(config);
  }

  private makeDefault(id: number): Promise<void> {
    return this.run(() => this.store.makeDefault(id), 'Default configuration changed');
  }

  private async delete(id: number): Promise<void> {
    const config = this.find(id);
    if (!config) return;
    const inChain = this.store.steps().some((s) => s.configId === id);
    const consequences = [
      ...(inChain ? ['Its attribution chain steps are removed with it.'] : []),
      ...(id === this.store.activeId() ? ['Another configuration becomes the default.'] : []),
    ];
    const ok = await this.confirm.confirm({
      title: `Delete ${config.name}?`,
      message: [...consequences, 'This cannot be undone.'].join(' '),
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
define('r2m-llm-settings-page', LlmSettingsPage);
