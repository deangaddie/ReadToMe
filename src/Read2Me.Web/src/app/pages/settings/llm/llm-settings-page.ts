import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { LlmServerConfig, toApiError } from '@app/api';
import { HasUnsavedChanges, confirmDiscard } from '@app/shared/unsaved-changes.guard';
import { ConfigList, ConfigListItem } from '@app/ui/config-list/config-list';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { LlmChainCard } from './chain-card';
import { LlmConfigEditor, LlmEditTarget } from './llm-config-editor';
import {
  API_TYPE_LABELS,
  EMPTY_LLM_FORM,
  duplicateName,
  sameLlmForm,
  toLlmForm,
} from './llm-config-form';
import { LlmSettingsStore } from './llm-settings-store';
import { LlmTestConsole } from './llm-test-console';

/**
 * `/settings/llm` (ticket 21): the settings master–detail template over LLM configs — list with
 * Make default / Duplicate / Delete on the left, the in-place editor on the right — then the
 * attribution escalation chain and the test console for the default config.
 */
@Component({
  selector: 'app-llm-settings-page',
  imports: [
    PageHeader,
    EmptyState,
    StatusChip,
    ConfigList,
    LlmConfigEditor,
    LlmChainCard,
    LlmTestConsole,
  ],
  providers: [LlmSettingsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header
      title="LLM"
      subtitle="Servers for attribution, voice prompts, AI book edits and character discovery."
    />

    @if (store.error(); as error) {
      <r2m-status-chip status="error" [label]="'LLM settings could not be loaded: ' + error" />
    }

    @if (store.configs(); as configs) {
      <div class="llm__master-detail">
        <r2m-config-list
          class="llm__list"
          activeLabel="Default"
          [items]="items()"
          [selectedId]="selectedId()"
          [busy]="busy()"
          (select)="select(+$event)"
          (create)="create()"
          (makeActive)="makeDefault(+$event)"
          (duplicate)="duplicate(+$event)"
          (delete)="delete(+$event)"
        />
        @if (target(); as target) {
          <app-llm-config-editor class="llm__editor" [target]="target" (saved)="onSaved($event)" />
        } @else {
          <r2m-empty-state
            class="llm__editor"
            icon="smart_toy"
            [headline]="configs.length ? 'Select a configuration' : 'No LLM configurations yet'"
            [hint]="configs.length ? 'Or create a new one.' : 'Create one to get started.'"
          />
        }
      </div>

      @if (configs.length) {
        <p class="llm__note">
          The <b>default</b> configuration is used for voice prompts, AI book edits and character
          discovery. Character attribution uses the attribution chain below instead.
        </p>
        <app-llm-chain-card />
      }

      @if (store.active(); as active) {
        <app-llm-test-console [config]="active" />
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .llm__master-detail {
      display: grid;
      grid-template-columns: 320px minmax(0, 1fr);
      gap: var(--r2m-space-4);
      align-items: start;
    }
    .llm__list {
      max-height: 70vh;
    }
    .llm__note {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    @media (max-width: 900px) {
      .llm__master-detail {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class LlmSettingsPage implements HasUnsavedChanges {
  protected readonly store = inject(LlmSettingsStore);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  private readonly editor = viewChild(LlmConfigEditor);

  /** Set on select / new / duplicate / save — never recomputed under an edit in progress. */
  protected readonly target = signal<LlmEditTarget | null>(null);
  protected readonly busy = signal(false);

  protected readonly selectedId = computed(() => {
    const id = this.target()?.id;
    return id ? String(id) : null;
  });

  protected readonly items = computed<ConfigListItem[]>(() =>
    (this.store.configs() ?? []).map((c) => ({
      id: String(c.id),
      name: c.name,
      subtitle: [API_TYPE_LABELS[c.apiType], c.model, c.baseUrl].filter(Boolean).join(' · '),
      isActive: c.id === this.store.activeId(),
    })),
  );

  constructor() {
    void this.store.load();
    // Keeps the editor on a config that still exists and, while it holds no edits, on its latest
    // saved state — a reload may carry a change made in Blazor or over the API.
    effect(() => {
      const configs = this.store.configs();
      if (!configs) return;
      untracked(() => this.reconcile(configs));
    });
  }

  hasUnsavedChanges(): boolean {
    return this.editor()?.dirty() ?? false;
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

  protected async select(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || id === this.target()?.id || !(await this.mayLeaveEditor())) return;
    this.open(config);
  }

  protected async create(): Promise<void> {
    if (!(await this.mayLeaveEditor())) return;
    this.target.set({ id: 0, baseline: EMPTY_LLM_FORM, draft: EMPTY_LLM_FORM });
  }

  /** Opens an unsaved copy in the editor: nothing is stored until Save. */
  protected async duplicate(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || !(await this.mayLeaveEditor())) return;
    const names = (this.store.configs() ?? []).map((c) => c.name);
    const draft = { ...toLlmForm(config), name: duplicateName(config.name, names) };
    this.target.set({ id: 0, baseline: EMPTY_LLM_FORM, draft });
  }

  protected onSaved(config: LlmServerConfig): void {
    this.open(config);
  }

  protected makeDefault(id: number): Promise<void> {
    return this.run(() => this.store.makeDefault(id), 'Default configuration changed');
  }

  protected async delete(id: number): Promise<void> {
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
