import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { toApiError } from '@app/api/api-client';
import { AppTheme } from '@app/api';
import { ThemeService } from '@app/theme/theme.service';
import { contrastText } from '@app/theme/theme-css';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { ThemeEditorData, ThemeEditorDialog, ThemeEditorResult } from './theme-editor-dialog';

/**
 * `/settings/themes` (ticket 04): card grid of every theme with Apply / Edit / Delete, New, and the
 * follow-system toggle. Writes go through ThemeService so the app restyles immediately and the
 * Blazor UI picks the same selection up on its next load.
 */
@Component({
  selector: 'app-themes-page',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatSlideToggleModule,
    MatTooltipModule,
    PageHeader,
    EmptyState,
    StatusChip,
  ],
  templateUrl: './themes-page.html',
  styleUrl: './themes-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemesPage {
  readonly themeService = inject(ThemeService);
  private readonly dialog = inject(MatDialog);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  readonly busyId = signal<number | null>(null);
  readonly busyGlobal = signal(false);

  readonly themes = this.themeService.themes;
  readonly followSystem = this.themeService.followSystem;
  readonly selectedId = this.themeService.selectedId;
  readonly effectiveId = computed(() => this.themeService.effective()?.id ?? null);
  readonly loadError = this.themeService.error;

  contrast(hex: string): string {
    return contrastText(hex);
  }

  isSelected(theme: AppTheme): boolean {
    return theme.id === this.selectedId();
  }

  /** The theme currently painting the app; differs from the selection while following the system. */
  isEffective(theme: AppTheme): boolean {
    return theme.id === this.effectiveId();
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
    const result = await this.openEditor({});
    if (!result) return;
    await this.run(null, () => this.themeService.create(result), `Created ${result.name}`);
  }

  /** Built-in themes open as a copy; custom ones edit in place. */
  async edit(theme: AppTheme): Promise<void> {
    const result = await this.openEditor({ theme });
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

  private openEditor(data: ThemeEditorData): Promise<ThemeEditorResult | undefined> {
    const ref = this.dialog.open<ThemeEditorDialog, ThemeEditorData, ThemeEditorResult>(
      ThemeEditorDialog,
      { data, autoFocus: 'input', width: '640px', maxWidth: '95vw' },
    );
    return new Promise((resolve) => ref.afterClosed().subscribe((r) => resolve(r)));
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
