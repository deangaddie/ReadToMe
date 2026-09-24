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
import { ActivatedRoute } from '@angular/router';
import { toApiError } from '@app/api';
import { HasUnsavedChanges, confirmDiscard } from '@app/shared/unsaved-changes.guard';
import { ConfigList, ConfigListItem } from '@app/ui/config-list/config-list';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { PROVIDER_AREAS, ProviderAreaKey, providerType } from './provider-area';
import { ProviderConfigEditor, ProviderEditTarget } from './provider-config-editor';
import {
  ProviderConfig,
  duplicateProviderForm,
  emptyProviderForm,
  sameProviderForm,
  toProviderForm,
} from './provider-form';
import { ProviderSettingsStore } from './provider-settings-store';
import { SimilarityTest, TranscriptionTest, VoiceDesignTest } from './provider-test-panels';
import { VoiceDesignSampleTextCard } from './voice-design-sample-text';

/** Route data key naming the area a `ProviderSettingsPage` route serves. */
export const PROVIDER_AREA_DATA = 'providerArea';

/**
 * `/settings/tts`, `/settings/voice-design`, `/settings/transcription` and `/settings/similarity`
 * (ticket 22): the settings master–detail template over one provider area's configs — list with
 * Make active / Duplicate / Delete on the left, the typed in-place editor on the right — then the
 * area's Test action for the selected saved config. Voice design leads with its sample text. The
 * route's `providerArea` data picks the area.
 */
@Component({
  selector: 'app-provider-settings-page',
  imports: [
    PageHeader,
    EmptyState,
    StatusChip,
    ConfigList,
    ProviderConfigEditor,
    VoiceDesignSampleTextCard,
    VoiceDesignTest,
    TranscriptionTest,
    SimilarityTest,
  ],
  providers: [ProviderSettingsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header [title]="area.title" [subtitle]="area.subtitle" />

    @if (store.error(); as error) {
      <r2m-status-chip
        status="error"
        [label]="area.title + ' settings could not be loaded: ' + error"
      />
    }

    @if (area.key === 'voice-design') {
      <app-voice-design-sample-text />
    }

    @if (store.configs(); as configs) {
      <div class="providers__master-detail">
        <r2m-config-list
          class="providers__list"
          [items]="items()"
          [selectedId]="selectedId()"
          [busy]="busy()"
          (select)="select(+$event)"
          (create)="create()"
          (makeActive)="makeActive(+$event)"
          (duplicate)="duplicate(+$event)"
          (delete)="delete(+$event)"
        />
        @if (target(); as target) {
          <app-provider-config-editor
            class="providers__editor"
            [area]="area"
            [target]="target"
            (saved)="open($event)"
          />
        } @else {
          <r2m-empty-state
            class="providers__editor"
            [icon]="area.icon"
            [headline]="
              configs.length ? 'Select a configuration' : 'No ' + area.noun + ' configurations yet'
            "
            [hint]="configs.length ? 'Or create a new one.' : 'Create one to get started.'"
          />
        }
      </div>

      @if (selected(); as config) {
        @switch (area.key) {
          @case ('voice-design') {
            <app-voice-design-test [config]="config" />
          }
          @case ('transcription') {
            <app-transcription-test [config]="config" />
          }
          @case ('semantic-similarity') {
            <app-similarity-test [config]="config" />
          }
        }
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .providers__master-detail {
      display: grid;
      grid-template-columns: 320px minmax(0, 1fr);
      gap: var(--r2m-space-4);
      align-items: start;
    }
    .providers__list {
      max-height: 70vh;
    }
    @media (max-width: 900px) {
      .providers__master-detail {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class ProviderSettingsPage implements HasUnsavedChanges {
  protected readonly area =
    PROVIDER_AREAS[inject(ActivatedRoute).snapshot.data[PROVIDER_AREA_DATA] as ProviderAreaKey];
  protected readonly store = inject(ProviderSettingsStore);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  private readonly editor = viewChild(ProviderConfigEditor);
  private readonly sampleText = viewChild(VoiceDesignSampleTextCard);

  /** Set on select / new / duplicate / save — never recomputed under an edit in progress. */
  protected readonly target = signal<ProviderEditTarget | null>(null);
  protected readonly busy = signal(false);

  protected readonly selectedId = computed(() => {
    const id = this.target()?.id;
    return id ? String(id) : null;
  });

  /** The saved config in the editor — what a Test action runs against. */
  protected readonly selected = computed(() => {
    const id = this.target()?.id;
    return (id && this.store.configs()?.find((c) => c.id === id)) || null;
  });

  protected readonly items = computed<ConfigListItem[]>(() =>
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

  constructor() {
    this.store.init(this.area);
    // Keeps the editor on a config that still exists and, while it holds no edits, on its latest
    // saved state — a reload may carry a change made in Blazor or over the API.
    effect(() => {
      const configs = this.store.configs();
      if (!configs) return;
      untracked(() => this.reconcile(configs));
    });
  }

  hasUnsavedChanges(): boolean {
    return this.editorDirty() || (this.sampleText()?.dirty() ?? false);
  }

  private editorDirty(): boolean {
    return this.editor()?.dirty() ?? false;
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

  protected open(config: ProviderConfig): void {
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

  protected async select(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || id === this.target()?.id || !(await this.mayLeaveEditor())) return;
    this.open(config);
  }

  protected async create(): Promise<void> {
    if (!(await this.mayLeaveEditor())) return;
    const empty = this.empty();
    this.target.set({ id: 0, baseline: empty, draft: empty });
  }

  /** Opens an unsaved copy in the editor: nothing is stored until Save. */
  protected async duplicate(id: number): Promise<void> {
    const config = this.find(id);
    if (!config || !(await this.mayLeaveEditor())) return;
    const names = (this.store.configs() ?? []).map((c) => c.name);
    const draft = duplicateProviderForm(this.form(config), names, () => crypto.randomUUID());
    this.target.set({ id: 0, baseline: this.empty(), draft });
  }

  protected makeActive(id: number): Promise<void> {
    return this.run(() => this.store.makeActive(id), 'Active configuration changed');
  }

  protected async delete(id: number): Promise<void> {
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
