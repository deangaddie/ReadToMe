import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { PromptCatalogEntry, PromptKind } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import type { HasUnsavedChanges } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { confirmDiscard } from '@app/shared/unsaved-changes';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, icon, statusChip } from '@app/ui/partials';
import type { PromptEditor } from './prompt-editor';
import { PromptsStore } from './prompts-store';
import css from './prompts-page.css' with { type: 'text' };
import './prompt-editor';

adoptStyles(css);

/**
 * `/settings/prompts` (ticket 23): the settings master–detail template over the eight prompt
 * kinds — the list marks stored overrides and compatibility warnings, the editor on the right
 * edits the selected kind. Refreshes on the hub's `settingsChanged { area: 'prompts' }`. Provides
 * the {@link PromptsStore} to the editor; the router's `guardUnsaved` asks
 * {@link hasUnsavedChanges} before leaving.
 */
export class PromptsPage extends R2mElement implements HasUnsavedChanges {
  readonly store = provide(this, PromptsStore, new PromptsStore());
  private readonly confirm = use(ConfirmService);

  readonly selectedKind = signal<PromptKind | null>(null);

  readonly selected = computed<PromptCatalogEntry | null>(() => {
    const kind = this.selectedKind();
    return (kind && this.store.catalog()?.find((e) => e.kind === kind)) || null;
  });

  protected override connected(): void {
    this.classList.add('prompts');
    void this.store.open();
    this.effect(() => {
      const first = this.store.catalog()?.[0];
      untracked(() => {
        if (first && !this.selectedKind()) this.selectedKind.set(first.kind);
      });
    });
    this.onDisconnect(() => this.store.close());
  }

  hasUnsavedChanges(): boolean {
    return this.editor()?.dirty() ?? false;
  }

  private editor(): PromptEditor | null {
    return this.querySelector<PromptEditor>('r2m-prompt-editor');
  }

  protected template() {
    const error = this.store.error();
    const catalog = this.store.catalog();
    const selectedKind = this.selectedKind();
    const selected = this.selected();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Prompts</h1>
        <p class="r2m-page-header__subtitle">
          The templates behind every LLM request. Tokens in double braces are filled in per request.
        </p>
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Prompts could not be loaded: ${error}` })
          : nothing
      }
      ${
        catalog
          ? html`<div class="r2m-master-detail">
              <ul class="prompts__list" role="listbox" aria-label="Prompt kinds">
                ${repeat(
                  catalog,
                  (entry) => entry.kind,
                  (entry) => this.row(entry, entry.kind === selectedKind),
                )}
              </ul>
              ${
                selected
                  ? html`<r2m-prompt-editor
                      class="prompts__editor"
                      .entry=${selected}
                    ></r2m-prompt-editor>`
                  : html`<div class="prompts__editor">
                      ${emptyState({
                        icon: 'chat',
                        headline: 'Select a prompt',
                        hint: 'Each kind is sent to the LLM at a different step.',
                      })}
                    </div>`
              }
            </div>`
          : nothing
      }
    `;
  }

  private row(entry: PromptCatalogEntry, selected: boolean) {
    return html`<li
      class="prompts__row ${selected ? 'prompts__row--selected' : ''}"
      role="option"
      aria-selected=${selected ? 'true' : 'false'}
    >
      <button
        type="button"
        class="prompts__main"
        data-kind=${entry.kind}
        @click=${() => void this.select(entry.kind)}
      >
        <span class="prompts__name">${entry.title}</span>
        <span class="prompts__meta">
          ${
            entry.isOverridden
              ? statusChip({ status: 'info', label: 'Overridden', compact: true })
              : nothing
          }
          ${
            entry.warnings.length
              ? html`<span
                  class="prompts__warning"
                  data-role="warning-icon"
                  role="img"
                  aria-label=${entry.warnings.join('. ')}
                  data-tooltip=${entry.warnings.join(' · ')}
                  >${icon('warning')}</span
                >`
              : nothing
          }
        </span>
      </button>
    </li>`;
  }

  private async select(kind: PromptKind): Promise<void> {
    if (kind === this.selectedKind()) return;
    if (this.hasUnsavedChanges() && !(await confirmDiscard(this.confirm))) return;
    this.selectedKind.set(kind);
  }
}
define('r2m-prompts-page', PromptsPage);
