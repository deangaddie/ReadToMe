import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AssemblyOutputDto, ProjectDetailDto, ProjectStatusDto } from '@app/api';
import { OverviewPage } from './overview-page';
import { ProjectStore } from './project-store';

const DETAIL: ProjectDetailDto = {
  folderName: 'dune',
  title: 'Dune',
  bookTitle: 'Dune',
  author: 'Frank Herbert',
  filename: 'dune.epub',
  fileType: 'Epub',
  coverImage: null,
  narratorOnlyMode: true,
  narrator: { characterId: 'w', displayName: 'Watson', isLinked: true },
};

function status(overrides: Partial<ProjectStatusDto> = {}): ProjectStatusDto {
  return {
    hasContent: true,
    characters: 2,
    charactersWithLines: 2,
    readyVoices: 1,
    items: { total: 8, withAudio: 2, unattributed: 3 },
    attribution: { remaining: 3, processing: false, queued: 0 },
    audio: { remaining: 4 },
    review: 0,
    volumeIds: ['v1', 'v2'],
    nodes: {
      v1: {
        attributionRemaining: 2,
        audioRemaining: 2,
        review: 0,
        attributionProcessing: false,
        attributionQueued: 0,
        isDone: false,
      },
      v2: {
        attributionRemaining: 1,
        audioRemaining: 2,
        review: 0,
        attributionProcessing: false,
        attributionQueued: 0,
        isDone: false,
      },
    },
    revision: 1,
    ...overrides,
  };
}

describe('OverviewPage', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [OverviewPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), ProjectStore],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    TestBed.inject(ProjectStore).close();
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function render(s = status(), outputs: AssemblyOutputDto[] = []) {
    const store = TestBed.inject(ProjectStore);
    const opened = store.open('dune');
    http.expectOne('/api/projects/dune').flush(DETAIL);
    http.expectOne('/api/projects/dune/status').flush(s);
    await opened;
    const fixture = TestBed.createComponent(OverviewPage);
    await settle();
    http.expectOne('/api/projects/dune/assembly/outputs').flush(outputs);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  function button(el: HTMLElement, action: string): HTMLButtonElement {
    return el.querySelector(`[data-action="${action}"]`) as HTMLButtonElement;
  }

  it('shows the stepper and the project card side by side', async () => {
    const { el } = await render();
    expect(el.querySelectorAll('.r2m-pipeline__step').length).toBe(6);
    expect(el.querySelector('[data-step="attribute"]')?.textContent).toContain('3 remaining');
    const details = el.querySelector('app-project-details-panel')!;
    expect(details.textContent).toContain('Narrated by Watson');
    expect(details.textContent).toContain('dune.epub');
  });

  it('a fresh project offers Read book as the next step and imports then refetches status', async () => {
    const { fixture, el } = await render(
      status({
        hasContent: false,
        volumeIds: [],
        nodes: {},
        items: { total: 0, withAudio: 0, unattributed: 0 },
      }),
    );
    expect(el.querySelector('[aria-current="step"]')?.getAttribute('data-step')).toBe('import');

    button(el, 'readBook').click();
    const req = http.expectOne({ method: 'POST', url: '/api/projects/dune/import' });
    expect(req.request.body).toEqual({ reread: false });
    req.flush(null);
    await settle();
    http.expectOne('/api/projects/dune/status').flush(status());
    await settle();
    await fixture.whenStable();

    expect(el.querySelector('[data-step="import"]')?.textContent).toContain('Done');
  });

  it('Attribute unprocessed enqueues every volume with unprocessedOnly', async () => {
    const { el } = await render();
    button(el, 'attribute').click();
    await settle();

    // One volume at a time, in book order.
    for (const nodeId of ['v1', 'v2']) {
      const req = http.expectOne({ method: 'POST', url: '/api/projects/dune/attribution/enqueue' });
      expect(req.request.body).toEqual({ level: 'volume', nodeId, unprocessedOnly: true });
      req.flush({ enqueued: 2 }, { status: 202, statusText: 'Accepted' });
      await settle();
    }
    await settle();
    http.expectOne('/api/projects/dune/status').flush(status());
    await settle();
  });

  it('Generate needed audio enqueues every volume with needsAudioOnly and the narrator-only policy', async () => {
    const { el } = await render();
    button(el, 'generateAudio').click();
    await settle();

    for (const nodeId of ['v1', 'v2']) {
      const req = http.expectOne({ method: 'POST', url: '/api/projects/dune/audio/enqueue' });
      expect(req.request.body).toEqual({
        level: 'volume',
        nodeId,
        needsAudioOnly: true,
        narratorOnlyMode: true,
      });
      req.flush({ enqueued: 1 }, { status: 202, statusText: 'Accepted' });
      await settle();
    }
    await settle();
    http.expectOne('/api/projects/dune/status').flush(status());
    await settle();
  });

  it('Reread asks for a destructive confirm before posting reread: true', async () => {
    const { el } = await render();
    button(el, 'reread').click();
    await settle();

    const dialog = document.querySelector('r2m-confirm-dialog')!;
    expect(dialog.classList.contains('r2m-confirm-dialog--destructive')).toBe(true);
    (dialog.querySelector('.r2m-confirm-dialog__confirm') as HTMLButtonElement).click();

    let req;
    for (let i = 0; i < 100 && !req; i++) {
      [req] = http.match({ method: 'POST', url: '/api/projects/dune/import' });
      if (!req) await settle(10);
    }
    expect(req?.request.body).toEqual({ reread: true });
    req!.flush(null);
    await settle();
    http.expectOne('/api/projects/dune/status').flush(status());
    await settle();
  });

  it('navigation actions route to cast, book modes and export', async () => {
    const { el } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    button(el, 'discover').click();
    expect(navigate).toHaveBeenLastCalledWith(['/projects', 'dune', 'cast'], {
      queryParams: { discover: 1 },
    });
    button(el, 'openSpeakers').click();
    expect(navigate).toHaveBeenLastCalledWith(['/projects', 'dune', 'book'], {
      queryParams: { mode: 'speakers' },
    });
    button(el, 'openAudioMode').click();
    expect(navigate).toHaveBeenLastCalledWith(['/projects', 'dune', 'book'], {
      queryParams: { mode: 'audio' },
    });
    button(el, 'assemble').click();
    expect(navigate).toHaveBeenLastCalledWith(['/projects', 'dune', 'export']);
  });

  it('editing the title inline PATCHes and shows the returned detail', async () => {
    const { fixture, el } = await render();
    const edit = el.querySelector('.details__title .r2m-inline-edit__display') as HTMLButtonElement;
    edit.click();
    await fixture.whenStable();
    const input = el.querySelector('.details__title input') as HTMLInputElement;
    input.value = 'Dune Messiah';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));

    const req = http.expectOne({ method: 'PATCH', url: '/api/projects/dune' });
    expect(req.request.body).toEqual({ title: 'Dune Messiah' });
    req.flush({ ...DETAIL, title: 'Dune Messiah' });
    await settle();
    await fixture.whenStable();

    expect(el.querySelector('r2m-page-header')?.textContent).toContain('Dune Messiah');
  });
});
