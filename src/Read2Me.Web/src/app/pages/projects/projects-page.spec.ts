import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ProjectSummary } from '@app/api';
import { ProjectsPage } from './projects-page';

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

describe('ProjectsPage', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectsPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  async function settle(ms = 0) {
    await new Promise((r) => setTimeout(r, ms));
  }

  /** MatDialog's afterClosed waits for the exit animation, so poll for the follow-on request. */
  async function expectEventually(method: string, url: string) {
    for (let i = 0; i < 100; i++) {
      const [req] = http.match({ method, url });
      if (req) return req;
      await settle(10);
    }
    throw new Error(`No ${method} ${url} request arrived`);
  }

  async function render(projects: ProjectSummary[]) {
    const fixture = TestBed.createComponent(ProjectsPage);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('.projects__skeleton').length).toBeGreaterThan(0);
    http.expectOne({ method: 'GET', url: '/api/projects' }).flush(projects);
    await settle();
    fixture.detectChanges();
    return { fixture, el };
  }

  it('shows skeletons, then a card per project sorted by title', async () => {
    const { el } = await render([emma, dune]);
    expect(el.querySelector('.projects__skeleton')).toBeNull();
    const cards = Array.from(el.querySelectorAll('r2m-project-card'));
    expect(cards.map((c) => c.getAttribute('data-folder'))).toEqual(['dune', 'emma']);
    expect(cards[0]?.textContent).toContain('Frank Herbert');
    expect(cards[1]?.textContent).toContain('Not read in yet');
  });

  it('shows the empty state with the primary action when there are no projects', async () => {
    const { el } = await render([]);
    expect(el.querySelector('r2m-empty-state')).not.toBeNull();
    expect(el.querySelectorAll('.projects__new').length).toBe(2);
  });

  it('opens a card by navigating to the overview', async () => {
    const { el } = await render([dune]);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    (el.querySelector('.r2m-project-card__cover') as HTMLButtonElement).click();

    expect(navigate).toHaveBeenCalledWith(['/projects', 'dune']);
  });

  it('delete asks for confirmation, then DELETEs and drops the card', async () => {
    const { fixture, el } = await render([dune, emma]);

    const deleting = fixture.componentInstance.delete(dune);
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    expect(dialog.textContent).toContain('Delete Dune?');
    expect(dialog.classList.contains('r2m-confirm-dialog--destructive')).toBe(true);
    (dialog.querySelector('.r2m-confirm-dialog__confirm') as HTMLButtonElement).click();

    const req = await expectEventually('DELETE', '/api/projects/dune');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await deleting;
    fixture.detectChanges();

    const cards = Array.from(el.querySelectorAll('r2m-project-card'));
    expect(cards.map((c) => c.getAttribute('data-folder'))).toEqual(['emma']);
  });

  it('cancelling the confirm sends nothing', async () => {
    const { fixture, el } = await render([dune]);

    const deleting = fixture.componentInstance.delete(dune);
    await settle();
    (document.querySelector('.r2m-confirm-dialog__cancel') as HTMLButtonElement).click();
    await deleting;
    fixture.detectChanges();

    http.expectNone({ method: 'DELETE', url: '/api/projects/dune' });
    expect(el.querySelectorAll('r2m-project-card').length).toBe(1);
  });
});
