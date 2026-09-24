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
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { PromptCatalogEntry, PromptKind } from '@app/api';
import { HasUnsavedChanges, confirmDiscard } from '@app/shared/unsaved-changes.guard';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { PromptEditor } from './prompt-editor';
import { PromptsStore } from './prompts-store';

/**
 * `/settings/prompts` (ticket 23): the settings master–detail template over the eight prompt
 * kinds — the list marks stored overrides and compatibility warnings, the editor on the right
 * edits the selected kind. Refreshes on the hub's `settingsChanged { area: 'prompts' }`.
 */
@Component({
  selector: 'app-prompts-page',
  imports: [MatIconModule, MatTooltipModule, PageHeader, EmptyState, StatusChip, PromptEditor],
  providers: [PromptsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header
      title="Prompts"
      subtitle="The templates behind every LLM request. Tokens in double braces are filled in per request."
    />

    @if (store.error(); as error) {
      <r2m-status-chip status="error" [label]="'Prompts could not be loaded: ' + error" />
    }

    @if (store.catalog(); as catalog) {
      <div class="prompts__master-detail">
        <ul class="prompts__list" role="listbox" aria-label="Prompt kinds">
          @for (entry of catalog; track entry.kind) {
            <li
              class="prompts__row"
              role="option"
              [attr.aria-selected]="entry.kind === selectedKind()"
              [class.prompts__row--selected]="entry.kind === selectedKind()"
            >
              <button
                type="button"
                class="prompts__main"
                [attr.data-kind]="entry.kind"
                (click)="select(entry.kind)"
              >
                <span class="prompts__name">{{ entry.title }}</span>
                <span class="prompts__meta">
                  @if (entry.isOverridden) {
                    <r2m-status-chip status="info" label="Overridden" compact />
                  }
                  @if (entry.warnings.length) {
                    <mat-icon
                      class="prompts__warning"
                      data-role="warning-icon"
                      [matTooltip]="entry.warnings.join(' · ')"
                      [attr.aria-label]="entry.warnings.join('. ')"
                    >
                      warning
                    </mat-icon>
                  }
                </span>
              </button>
            </li>
          }
        </ul>

        @if (selected(); as entry) {
          <app-prompt-editor class="prompts__editor" [entry]="entry" />
        } @else {
          <r2m-empty-state
            class="prompts__editor"
            icon="chat"
            headline="Select a prompt"
            hint="Each kind is sent to the LLM at a different step."
          />
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .prompts__master-detail {
      display: grid;
      grid-template-columns: 320px minmax(0, 1fr);
      gap: var(--r2m-space-4);
      align-items: start;
    }
    .prompts__list {
      margin: 0;
      padding: var(--r2m-space-1);
      list-style: none;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .prompts__main {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--r2m-space-2);
      width: 100%;
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 0;
      border-radius: var(--r2m-radius-md);
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .prompts__main:hover {
      background: var(--r2m-surface-high);
    }
    .prompts__row--selected .prompts__main {
      background: var(--r2m-surface-high);
      font-weight: 600;
    }
    .prompts__meta {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-1);
      flex-shrink: 0;
    }
    .prompts__warning {
      color: var(--r2m-status-warn);
      font-size: 20px;
      width: 20px;
      height: 20px;
    }
    @media (max-width: 900px) {
      .prompts__master-detail {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class PromptsPage implements HasUnsavedChanges {
  protected readonly store = inject(PromptsStore);
  private readonly confirm = inject(ConfirmService);
  private readonly editor = viewChild(PromptEditor);

  protected readonly selectedKind = signal<PromptKind | null>(null);

  protected readonly selected = computed<PromptCatalogEntry | null>(() => {
    const kind = this.selectedKind();
    return (kind && this.store.catalog()?.find((e) => e.kind === kind)) || null;
  });

  constructor() {
    effect(() => {
      const first = this.store.catalog()?.[0];
      untracked(() => {
        if (first && !this.selectedKind()) this.selectedKind.set(first.kind);
      });
    });
  }

  hasUnsavedChanges(): boolean {
    return this.editor()?.dirty() ?? false;
  }

  protected async select(kind: PromptKind): Promise<void> {
    if (kind === this.selectedKind()) return;
    if (this.hasUnsavedChanges() && !(await confirmDiscard(this.confirm))) return;
    this.selectedKind.set(kind);
  }
}
