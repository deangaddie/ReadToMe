import { COVER_ACCEPT, COVER_MAX_BYTES, type ProjectSummary, toApiError } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { use } from '@app/core/services';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { ProjectsStore } from './projects-store';
import {
  deleteSeriesConfirm,
  leaveConfirm,
  moveConfirm,
  removePosterConfirm,
} from './permanent-changes';
import type { SeriesPick } from './series-pickers';
import { bookLabel, type SeriesView } from './series-shelf';
import './series-pickers';

/**
 * The Projects page's series flows (Series lifecycle, #206; #207 7): the pickers, the name prompt,
 * the confirms, then the write through {@link ProjectsStore}. Creating, renaming and joining ask
 * nothing; Leave and Move to ask Leave's confirm; deleting a series asks in red. A refusal from the
 * host is a toast; a taken name asks for the name again.
 */
export class SeriesActions {
  private readonly store = use(ProjectsStore);
  private readonly confirm = use(ConfirmService);
  private readonly prompt = use(PromptService);
  private readonly toast = use(ToastService);

  /**
   * Asks for a series name and runs `write` with it; a 409 (name taken) toasts and asks again with
   * the typed name. Resolves true once written, false on Cancel or any other refusal.
   */
  private async withName(
    options: { title: string; initial?: string; confirmLabel: string; hint?: string },
    write: (name: string) => Promise<void>,
  ): Promise<boolean> {
    let initial = options.initial ?? '';
    for (;;) {
      const name = await this.prompt.text({
        title: options.title,
        label: 'Series name',
        initial,
        required: true,
        confirmLabel: options.confirmLabel,
        ...(options.hint ? { hint: options.hint } : {}),
      });
      if (name === null) return false;
      try {
        await write(name);
        return true;
      } catch (error) {
        const problem = toApiError(error);
        this.toast.problem(problem.toProblem());
        if (problem.status !== 409) return false;
        initial = name;
      }
    }
  }

  /** New series from the header: name only, it starts empty. */
  async newSeries(): Promise<void> {
    await this.withName({ title: 'New series', confirmLabel: 'Create series' }, async (name) => {
      await this.store.createSeries(name);
      this.toast.success(`Created series ${name}`);
    });
  }

  async rename(series: SeriesView): Promise<void> {
    await this.withName(
      { title: `Rename ${series.name}`, initial: series.name, confirmLabel: 'Rename' },
      async (name) => {
        if (name !== series.name) await this.store.renameSeries(series.id, name);
      },
    );
  }

  async deleteSeries(series: SeriesView): Promise<boolean> {
    const ok = await this.confirm.confirm(
      deleteSeriesConfirm({
        name: series.name,
        titles: series.members.map((m) => m.project.title),
        hasPoster: series.poster.source === 'Uploaded',
      }),
    );
    if (!ok) return false;
    return this.run(async () => {
      await this.store.deleteSeries(series.id);
      this.toast.success(`Deleted series ${series.name}`);
    });
  }

  /**
   * Upload/Replace poster…: picking the file is the confirm (Series lifecycle 11). A file outside
   * the cover's rules is turned away here, before it is sent.
   */
  async uploadPoster(series: SeriesView, file: File): Promise<void> {
    const extension = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();
    if (!COVER_ACCEPT.split(',').includes(extension)) {
      this.toast.warn('Use a .jpg, .png or .webp image.');
      return;
    }
    if (file.size > COVER_MAX_BYTES) {
      this.toast.warn('Poster image must be 10 MB or smaller.');
      return;
    }
    await this.run(() => this.store.uploadPoster(series.id, file));
  }

  /** Remove poster, behind its red confirm: the uploaded image has no History. */
  async removePoster(series: SeriesView): Promise<void> {
    const first = series.members[0];
    const ok = await this.confirm.confirm(
      removePosterConfirm({
        name: series.name,
        fallbackPosition: first?.project.coverImage ? first.position : null,
      }),
    );
    if (!ok) return;
    await this.run(() => this.store.clearPoster(series.id));
  }

  /** Remove from <series>, behind Leave's confirm. */
  async leave(project: ProjectSummary): Promise<void> {
    const current = this.store.membership().get(project.folderName);
    if (!current) return;
    const ok = await this.confirm.confirm(
      leaveConfirm({ title: project.title, seriesName: current.name, linked: [] }),
    );
    if (!ok) return;
    await this.run(async () => {
      await this.store.leave(current.seriesId, project.folderName);
      this.toast.success(`Removed ${project.title} from ${current.name}`);
    });
  }

  /** Add to series… (or Move to another series…) for a book: pick a series, or make a new one. */
  async addToSeries(project: ProjectSummary): Promise<void> {
    const picker = document.createElement('r2m-series-picker');
    picker.data = {
      book: { folder: project.folderName, title: project.title },
      series: [...this.store.series()].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      ),
      membership: this.store.membership(),
    };
    const pick = await openDialog<SeriesPick>(picker);
    if (!pick) return;
    if ('newSeries' in pick) {
      await this.newSeriesWith(project);
      return;
    }
    const target = this.store.series().find((s) => s.id === pick.seriesId);
    if (target) await this.join(target, project);
  }

  /** Add book… from a series: pick a book; one in another series moves, behind Leave's confirm. */
  async addBook(series: SeriesView): Promise<void> {
    const picker = document.createElement('r2m-book-picker');
    picker.data = {
      target: series,
      projects: this.store.projects(),
      membership: this.store.membership(),
    };
    const folder = await openDialog<string>(picker);
    if (!folder) return;
    const project = this.store.projects().find((p) => p.folderName === folder);
    if (project) await this.join(series, project);
  }

  /** Joins, or moves with Leave's confirm when the book is in another series; toasts the number. */
  private async join(target: SeriesView, project: ProjectSummary): Promise<void> {
    const current = this.store.membership().get(project.folderName);
    if (current?.seriesId === target.id) return;
    if (current && !(await this.confirmMove(project, current.name, target.name))) return;
    await this.run(async () => {
      const position = await this.store.join(target.id, project.folderName, current !== undefined);
      this.joinedToast(project, position, target.name);
    });
  }

  /**
   * New series… from a book's picker: it becomes Book 1. A book in another series is asked to
   * move first; the new series is then created on its own (a taken name asks again) and the book
   * moves into it, so a refused name never leaves the book half moved.
   */
  private async newSeriesWith(project: ProjectSummary): Promise<void> {
    const current = this.store.membership().get(project.folderName);
    if (current && !(await this.confirmMove(project, current.name, 'a new series'))) return;
    // Set inside the prompt's write; a holder, since TypeScript doesn't follow the callback.
    const created: { series?: { id: string; name: string } } = {};
    await this.withName(
      {
        title: 'New series',
        confirmLabel: 'Create series',
        hint: `${project.title} becomes Book 1.`,
      },
      async (name) => {
        if (current) created.series = { id: await this.store.createSeries(name), name };
        else {
          await this.store.createSeries(name, project.folderName);
          this.joinedToast(project, 1, name);
        }
      },
    );
    const target = created.series;
    if (!target) return;
    await this.run(async () => {
      const position = await this.store.join(target.id, project.folderName, true);
      this.joinedToast(project, position, target.name);
    });
  }

  private confirmMove(project: ProjectSummary, from: string, to: string): Promise<boolean> {
    return this.confirm.confirm(
      moveConfirm({ title: project.title, seriesName: from, to, linked: [] }),
    );
  }

  private joinedToast(project: ProjectSummary, position: number, seriesName: string): void {
    this.toast.success(
      `${project.title} is ${bookLabel(position)} of ${seriesName}. ` +
        'Edit the number if it belongs elsewhere.',
    );
  }

  /** Runs a write, handing a refusal to the toast; resolves whether it landed. */
  private async run(write: () => Promise<void>): Promise<boolean> {
    try {
      await write();
      return true;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    }
  }
}
