import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { ProjectSummary } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import type { ProjectsPage } from './projects-page';
import './projects-page';

const dune: ProjectSummary = {
  folderName: 'dune',
  title: 'Dune',
  author: 'Frank Herbert',
  coverImage: 'cover.jpg',
  audioItemTotal: 100,
  audioItemDone: 40,
  audioPercent: 40,
  fileType: 'Epub',
};
const emma: ProjectSummary = {
  folderName: 'emma',
  title: 'Emma',
  author: null,
  coverImage: null,
  audioItemTotal: 0,
  audioItemDone: 0,
  audioPercent: 0,
  fileType: 'Text',
};

let api: FakeApi;
let navigation: FakeNavigation;
let toasts: string[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  navigation = installNavigation('projects');
  const router = new Router();
  override(Router, router);
  router.start([{ path: 'projects', tag: 'x-nothing' }], async () => true);
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** Mounts the page over a pending list, checks the skeletons, then answers the list. */
async function render(projects: ProjectSummary[]) {
  let answer!: (value: ProjectSummary[]) => void;
  api.on('GET', '/api/projects', () => new Promise<ProjectSummary[]>((r) => (answer = r)));
  const page = document.createElement('r2m-projects-page');
  document.body.append(page);
  await page.rendered();
  await settle();
  expect(page.querySelectorAll('.projects__skeleton').length).toBeGreaterThan(0);
  answer(projects);
  await settle();
  await page.rendered();
  return page;
}

const cards = (page: ProjectsPage) =>
  Array.from(page.querySelectorAll('.r2m-project-card'), (c) => c.getAttribute('data-folder'));

describe('r2m-projects-page', () => {
  it('shows skeletons, then a card per project sorted by title', async () => {
    const page = await render([emma, dune]);
    expect(page.querySelector('.projects__skeleton')).toBeNull();
    expect(cards(page)).toEqual(['dune', 'emma']);
    const [first, second] = Array.from(page.querySelectorAll('.r2m-project-card'));
    expect(first?.textContent).toContain('Frank Herbert');
    expect(second?.textContent).toContain('Not read in yet');
  });

  it('shows the empty state with the primary action when there are no projects', async () => {
    const page = await render([]);
    expect(page.querySelector('.r2m-empty-state')).not.toBeNull();
    expect(page.querySelectorAll('.projects__new').length).toBe(2);
  });

  it('shows the load failure as an error chip, not a toast', async () => {
    api.on('GET', '/api/projects', () => problem(500, 'disk gone'));
    const page = document.createElement('r2m-projects-page');
    document.body.append(page);
    await settle();
    await page.rendered();
    expect(page.querySelector('.r2m-status-chip--error')?.textContent).toContain(
      'Projects could not be loaded',
    );
    expect(toasts).toEqual([]);
  });

  it('opens a card by navigating to the overview', async () => {
    const page = await render([dune]);

    page.querySelector<HTMLButtonElement>('.r2m-project-card__cover')!.click();

    expect(navigation.calls.map((c) => c.url)).toEqual(['http://localhost/app2/projects/dune']);
  });

  it('delete asks for confirmation, then DELETEs and drops the card', async () => {
    const page = await render([dune, emma]);
    api.on('DELETE', '/api/projects/dune', undefined);

    page.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    await dialog.rendered();
    expect(dialog.textContent).toContain('Delete Dune?');
    expect(dialog.querySelector('.r2m-confirm-dialog__confirm')?.classList).toContain(
      'r2m-button--danger',
    );
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!.click();
    await settle();
    await page.rendered();

    expect(api.calls('DELETE', '/api/projects/dune')).toHaveLength(1);
    expect(cards(page)).toEqual(['emma']);
    expect(toasts).toEqual(['Deleted Dune']);
  });

  it('cancelling the confirm sends nothing', async () => {
    const page = await render([dune]);

    page.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    await dialog.rendered();
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__cancel')!.click();
    await settle();
    await page.rendered();

    expect(api.calls('DELETE', '/api/projects/dune')).toHaveLength(0);
    expect(cards(page)).toEqual(['dune']);
  });
});
