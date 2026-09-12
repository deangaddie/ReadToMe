import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { Router } from '@angular/router';
import { ProjectSummary } from '@app/api';
import { toApiError } from '@app/api/api-client';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { ProjectCard } from '@app/ui/project-card/project-card';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { NewProjectDialog, NewProjectResult } from './new-project-dialog';
import { ProjectsStore } from './projects-store';

/** Skeleton cards drawn while the first load is in flight. */
const SKELETON_COUNT = 6;

/**
 * `/projects` (design §6.1, ticket 08): the shelf. Card grid over {@link ProjectsStore}, New project
 * dialog, and delete with a destructive confirm. Opening a card navigates to the overview.
 */
@Component({
  selector: 'app-projects-page',
  imports: [MatButtonModule, MatIconModule, PageHeader, EmptyState, StatusChip, ProjectCard],
  templateUrl: './projects-page.html',
  styleUrl: './projects-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectsPage {
  readonly store = inject(ProjectsStore);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  readonly projects = this.store.projects;
  readonly skeletons = Array.from({ length: SKELETON_COUNT }, (_, i) => i);
  /** Folder of the project whose delete is in flight; its card is disabled meanwhile. */
  readonly deleting = signal<string | null>(null);

  constructor() {
    // Refresh on every visit; a failure surfaces through the store's error signal, not a toast.
    void this.store.load().catch(() => undefined);
  }

  showSkeleton(): boolean {
    return this.store.loading() && !this.store.loaded();
  }

  open(project: ProjectSummary): void {
    void this.router.navigate(['/projects', project.folderName]);
  }

  async create(): Promise<void> {
    const ref = this.dialog.open<NewProjectDialog, void, NewProjectResult>(NewProjectDialog, {
      autoFocus: 'input',
      maxWidth: '95vw',
    });
    const folder = await new Promise<NewProjectResult | undefined>((resolve) =>
      ref.afterClosed().subscribe((r) => resolve(r)),
    );
    if (!folder) return;
    this.toast.success('Project created');
    void this.router.navigate(['/projects', folder]);
  }

  async delete(project: ProjectSummary): Promise<void> {
    const ok = await this.confirm.confirm({
      title: `Delete ${project.title}?`,
      message:
        'The project folder and everything in it are removed: the book file, characters, ' +
        'voices and generated audio. This cannot be undone.',
      destructive: true,
      confirmLabel: 'Delete project',
    });
    if (!ok) return;
    this.deleting.set(project.folderName);
    try {
      await this.store.delete(project.folderName);
      this.toast.success(`Deleted ${project.title}`);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.deleting.set(null);
    }
  }
}
