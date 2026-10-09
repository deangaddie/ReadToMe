import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { type ProjectSummary, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, icon, statusChip } from '@app/ui/partials';
import { projectCard } from '@app/ui/project-card';
import { ToastService } from '@app/ui/toast';
import { openNewProjectDialog } from './new-project-dialog';
import { ProjectsStore } from './projects-store';
import projectsCss from './projects-page.css' with { type: 'text' };

adoptStyles(projectsCss);

/** Skeleton cards drawn while the first load is in flight. */
const SKELETONS = [0, 1, 2, 3, 4, 5];

/**
 * `/projects` (design §6.1): the shelf. Card grid over {@link ProjectsStore}, the New project
 * dialog, and delete with a destructive confirm. Opening a card navigates to the overview.
 */
export class ProjectsPage extends R2mElement {
  private readonly store = use(ProjectsStore);
  private readonly router = use(Router);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);

  /** Folder of the project whose delete is in flight; its card is disabled meanwhile. */
  private readonly deleting = signal<string | null>(null);

  protected override connected(): void {
    this.classList.add('projects');
    // Refresh on every visit; a failure surfaces through the store's error signal, not a toast.
    void this.store.load().catch(() => undefined);
  }

  protected template() {
    const error = this.store.error();
    const projects = this.store.projects();
    const newButton = html`<button
      type="button"
      class="r2m-button r2m-button--filled projects__new"
      @click=${() => void this.create()}
    >
      ${icon('add')} New project
    </button>`;
    return html`
      <header class="r2m-page-header projects__header">
        <h1 class="r2m-page-header__title">Projects</h1>
        <p class="r2m-page-header__subtitle">Every audiobook in the workspace.</p>
        <span class="projects__actions">${newButton}</span>
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Projects could not be loaded: ${error}` })
          : nothing
      }
      ${
        this.store.loading() && !this.store.loaded()
          ? html`<div class="projects__grid" aria-busy="true" aria-label="Loading projects">
              ${SKELETONS.map(
                () => html`<div class="projects__skeleton">
                  <div class="projects__skeleton-cover"></div>
                  <div class="projects__skeleton-line projects__skeleton-line--title"></div>
                  <div class="projects__skeleton-line"></div>
                </div>`,
              )}
            </div>`
          : projects.length === 0 && !error
            ? emptyState(
                {
                  icon: 'library_books',
                  headline: 'No projects yet',
                  hint: 'Import an epub or text file to create your first audiobook project.',
                },
                newButton,
              )
            : html`<div class="projects__grid">
                ${repeat(
                  projects,
                  (p) => p.folderName,
                  (p) =>
                    projectCard(p, {
                      onOpen: () => this.open(p),
                      onDelete: () => void this.delete(p),
                      deleting: this.deleting() === p.folderName,
                    }),
                )}
              </div>`
      }
    `;
  }

  open(project: ProjectSummary): void {
    this.router.navigate(`projects/${encodeURIComponent(project.folderName)}`);
  }

  async create(): Promise<void> {
    const folder = await openNewProjectDialog();
    if (!folder) return;
    this.toast.success('Project created');
    this.router.navigate(`projects/${encodeURIComponent(folder)}`);
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
define('r2m-projects-page', ProjectsPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-projects-page': ProjectsPage;
  }
}
