import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { styleMap } from 'lit-html/directives/style-map.js';
import { type AppTheme, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { contrastText } from '@app/theme/theme-css';
import { ThemeService } from '@app/theme/theme.service';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { ThemeEditor } from './theme-editor-dialog';
import css from './themes-page.css' with { type: 'text' };

adoptStyles(css);

/** The optional colours a card swatches, in Angular's order, with their tooltips. */
const SWATCHES: { key: keyof AppTheme; tooltip: string }[] = [
  { key: 'primary', tooltip: 'Primary' },
  { key: 'secondary', tooltip: 'Secondary' },
  { key: 'background', tooltip: 'Background' },
  { key: 'surface', tooltip: 'Surface' },
  { key: 'appbarBackground', tooltip: 'App bar' },
  { key: 'drawerBackground', tooltip: 'Nav rail' },
];

/** One theme card's inputs, derived in the render effect (see {@link ThemesPage.cards}). */
interface ThemeCard {
  theme: AppTheme;
  selected: boolean;
  effective: boolean;
  busy: boolean;
  busyGlobal: boolean;
}

/**
 * `/settings/themes` (ticket 04): card grid of every theme with Apply / Edit / Delete, New, and the
 * follow-system toggle. Writes go through {@link ThemeService} so the app restyles immediately and
 * every other tab picks the same selection up on its next load.
 */
export class ThemesPage extends R2mElement {
  readonly themeService = use(ThemeService);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private readonly editor = use(ThemeEditor);

  readonly busyId = signal<number | null>(null);
  readonly busyGlobal = signal(false);

  readonly themes = this.themeService.themes;
  readonly followSystem = this.themeService.followSystem;
  readonly selectedId = this.themeService.selectedId;
  readonly effectiveId = computed(() => this.themeService.effective()?.id ?? null);
  readonly loadError = this.themeService.error;

  protected override connected(): void {
    this.classList.add('themes');
  }

  isSelected(theme: AppTheme): boolean {
    return theme.id === this.selectedId();
  }

  /** The theme currently painting the app; differs from the selection while following the system. */
  isEffective(theme: AppTheme): boolean {
    return theme.id === this.effectiveId();
  }

  protected template() {
    const themes = this.themes();
    const loadError = this.loadError();
    const busyGlobal = this.busyGlobal();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Themes</h1>
        <p class="r2m-page-header__subtitle">The applied theme styles the whole app.</p>
        <span class="r2m-page-header__actions">
          <label
            class="r2m-switch"
            data-tooltip="Use the built-in Light or Dark theme to match the operating system"
          >
            <input
              type="checkbox"
              role="switch"
              name="followSystem"
              .checked=${this.followSystem()}
              ?disabled=${busyGlobal}
              @change=${(e: Event) =>
                void this.setFollowSystem((e.target as HTMLInputElement).checked)}
            />
            Follow system
          </label>
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="new-theme"
            ?disabled=${busyGlobal}
            @click=${() => void this.create()}
          >
            ${icon('add')} New theme
          </button>
        </span>
      </header>

      ${
        loadError
          ? statusChip({ status: 'error', label: `Themes could not be loaded: ${loadError}` })
          : nothing
      }
      ${
        themes.length === 0
          ? emptyState({
              icon: 'palette',
              headline: 'No themes',
              hint: 'The host seeds the built-in themes on first request.',
            })
          : html`<div class="themes__grid">
              ${repeat(
                this.cards(themes),
                (card) => card.theme.id,
                (card) => this.card(card),
              )}
            </div>`
      }
    `;
  }

  /**
   * Selection and busy state are read here, in the render effect: lit's `repeat` runs its item
   * template at commit time, outside the effect, so a signal first read there is not tracked.
   */
  private cards(themes: AppTheme[]): ThemeCard[] {
    const busyGlobal = this.busyGlobal();
    const busyId = this.busyId();
    return themes.map((theme) => ({
      theme,
      selected: this.isSelected(theme),
      effective: this.isEffective(theme),
      busy: busyId === theme.id || busyGlobal,
      busyGlobal,
    }));
  }

  private card({ theme, selected, effective, busy, busyGlobal }: ThemeCard) {
    return html`<article
      class="themes__card ${selected ? 'themes__card--selected' : ''}"
      data-theme-id=${theme.id}
    >
      <div
        class="themes__strip"
        style=${styleMap({
          background: `linear-gradient(100deg, ${theme.primary} 0 55%, ${theme.secondary} 55% 100%)`,
        })}
        aria-hidden="true"
      >
        ${
          theme.isBuiltIn
            ? html`<span
                class="themes__lock"
                style=${styleMap({ color: contrastText(theme.primary) })}
                data-tooltip="Built-in: read-only"
                >${icon('lock')}</span
              >`
            : nothing
        }
      </div>

      <div class="themes__body">
        <div class="themes__title-row">
          <h3 class="themes__name">${theme.name}</h3>
          ${statusChip({
            status: 'neutral',
            icon: theme.isDark ? 'dark_mode' : 'light_mode',
            label: theme.isDark ? 'Dark' : 'Light',
            compact: true,
          })}
          ${selected ? statusChip({ status: 'ok', label: 'Active', compact: true }) : nothing}
          ${
            effective && !selected
              ? statusChip({ status: 'info', label: 'In use (system)', compact: true })
              : nothing
          }
        </div>

        <div class="themes__swatches" aria-label="Colours">
          ${SWATCHES.map(({ key, tooltip }) => {
            const colour = theme[key];
            return typeof colour === 'string' && colour
              ? html`<span
                  class="themes__swatch"
                  style=${styleMap({ background: colour })}
                  data-tooltip=${tooltip}
                ></span>`
              : nothing;
          })}
        </div>
      </div>

      <div class="themes__actions">
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="apply"
          ?disabled=${selected || busy}
          @click=${() => void this.apply(theme)}
        >
          ${selected ? 'Applied' : 'Apply'}
        </button>
        <button
          type="button"
          class="r2m-icon-button"
          data-action="edit"
          ?disabled=${busyGlobal}
          aria-label=${theme.isBuiltIn ? `Duplicate ${theme.name}` : `Edit ${theme.name}`}
          data-tooltip=${theme.isBuiltIn ? 'Duplicate and edit' : 'Edit'}
          @click=${() => void this.edit(theme)}
        >
          ${icon(theme.isBuiltIn ? 'content_copy' : 'edit')}
        </button>
        ${
          theme.isBuiltIn
            ? nothing
            : html`<button
                type="button"
                class="r2m-icon-button themes__delete"
                data-action="delete"
                ?disabled=${busy}
                aria-label="Delete ${theme.name}"
                data-tooltip="Delete"
                @click=${() => void this.delete(theme)}
              >
                ${icon('delete')}
              </button>`
        }
      </div>
    </article>`;
  }

  async apply(theme: AppTheme): Promise<void> {
    await this.run(theme.id, () => this.themeService.applyTheme(theme.id), `Applied ${theme.name}`);
  }

  async setFollowSystem(follow: boolean): Promise<void> {
    this.busyGlobal.set(true);
    try {
      await this.themeService.setFollowSystem(follow);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.busyGlobal.set(false);
    }
  }

  async create(): Promise<void> {
    const result = await this.editor.open({});
    if (!result) return;
    await this.run(null, () => this.themeService.create(result), `Created ${result.name}`);
  }

  /** Built-in themes open as a copy; custom ones edit in place. */
  async edit(theme: AppTheme): Promise<void> {
    const result = await this.editor.open({ theme });
    if (!result) return;
    if (theme.isBuiltIn) {
      await this.run(null, () => this.themeService.create(result), `Created ${result.name}`);
    } else {
      await this.run(
        theme.id,
        () => this.themeService.update({ ...result, id: theme.id, isBuiltIn: false }),
        `Saved ${result.name}`,
      );
    }
  }

  async delete(theme: AppTheme): Promise<void> {
    if (theme.isBuiltIn) return;
    const ok = await this.confirm.confirm({
      title: `Delete ${theme.name}?`,
      message: this.isSelected(theme)
        ? 'This theme is active; the app falls back to the built-in Light theme.'
        : 'This cannot be undone.',
      destructive: true,
      confirmLabel: 'Delete',
    });
    if (!ok) return;
    await this.run(theme.id, () => this.themeService.delete(theme.id), `Deleted ${theme.name}`);
  }

  private async run(
    id: number | null,
    action: () => Promise<unknown>,
    success: string,
  ): Promise<void> {
    this.busyId.set(id);
    this.busyGlobal.set(id === null);
    try {
      await action();
      this.toast.success(success);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.busyId.set(null);
      this.busyGlobal.set(false);
    }
  }
}
define('r2m-themes-page', ThemesPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-themes-page': ThemesPage;
  }
}
