import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AssemblyOutputDto, ProjectStatusDto } from '@app/api';
import { ProjectStore } from '../project/project-store';
import { ExportPage } from './export-page';

const STATUS: ProjectStatusDto = {
  hasContent: true,
  characters: 1,
  charactersWithLines: 1,
  readyVoices: 1,
  items: { total: 8, withAudio: 5, unattributed: 0 },
  attribution: { remaining: 0, processing: false, queued: 0 },
  audio: { remaining: 2 },
  review: 0,
  volumeIds: [],
  nodes: {},
  revision: 1,
};

const OUTPUTS: AssemblyOutputDto[] = [
  {
    fileName: 'Dune_partial_20260919.m4b',
    sizeBytes: 1536,
    createdAt: '2026-09-19T10:00:00+00:00',
    isPartial: true,
  },
  {
    fileName: 'Dune.m4b',
    sizeBytes: 2048,
    createdAt: '2026-09-01T10:00:00+00:00',
    isPartial: false,
  },
];

describe('ExportPage', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ExportPage],
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

  async function render(outputs: AssemblyOutputDto[] = OUTPUTS) {
    const store = TestBed.inject(ProjectStore);
    const opened = store.open('dune');
    http.expectOne('/api/projects/dune').flush({ folderName: 'dune', title: 'Dune' });
    http.expectOne('/api/projects/dune/status').flush(STATUS);
    await opened;
    const fixture = TestBed.createComponent(ExportPage);
    await settle();
    http.expectOne('/api/projects/dune/assembly/outputs').flush(outputs);
    http
      .expectOne('/api/settings/audio-processing')
      .flush({ ffmpegPath: 'D:\\ffmpeg\\ffmpeg.exe' });
    await settle();
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  /** The dialog resolves only after its close animation, so the follow-up request is polled for. */
  async function eventually(method: string, url: string): Promise<TestRequest> {
    for (let i = 0; i < 100; i++) {
      const [req] = http.match({ method, url });
      if (req) return req;
      await settle(10);
    }
    throw new Error(`No ${method} ${url}`);
  }

  const startRequest = () => http.expectOne({ method: 'POST', url: '/api/projects/dune/assembly' });

  it('shows readiness and the outputs with size, partial chip and a download link', async () => {
    const { el } = await render();

    const readiness = el.querySelector('[data-card="readiness"]')!.textContent;
    expect(readiness).toContain('5 of 8');
    expect(readiness).toContain('D:\\ffmpeg\\ffmpeg.exe');

    const rows = el.querySelectorAll('[data-output]');
    expect(rows.length).toBe(2);
    expect(rows[0]!.textContent).toContain('1.5 KB');
    expect(rows[0]!.textContent).toContain('Partial');
    expect(rows[1]!.textContent).not.toContain('Partial');
    expect(rows[1]!.querySelector('a[data-action="download-output"]')!.getAttribute('href')).toBe(
      '/api/projects/dune/assembly/outputs/Dune.m4b',
    );
    expect(el.querySelector('[data-card="progress"]')).toBeNull();
  });

  it('turns the missing-audio 409 into the partial prompt and then starts with allowPartial', async () => {
    const { el } = await render();

    (el.querySelector('[data-action="assemble"]') as HTMLButtonElement).click();
    await settle();
    const first = startRequest();
    expect(first.request.body).toEqual({ allowPartial: false });
    first.flush(
      {
        title: 'Conflict',
        status: 409,
        detail: '3 items still need audio.',
        audioRemainingCount: 3,
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    await settle();

    const dialog = document.querySelector('r2m-confirm-dialog')!;
    expect(dialog.classList.contains('r2m-confirm-dialog--destructive')).toBe(true);
    expect(dialog.textContent).toContain('Assemble partial — 3 items missing audio');
    (dialog.querySelector('.r2m-confirm-dialog__confirm') as HTMLButtonElement).click();

    const second = await eventually('POST', '/api/projects/dune/assembly');
    expect(second.request.body).toEqual({ allowPartial: true });
    second.flush({ started: true }, { status: 202, statusText: 'Accepted' });
    await settle();
  });

  it('starts nothing when the partial prompt is declined', async () => {
    const { fixture, el } = await render();

    (el.querySelector('[data-action="assemble"]') as HTMLButtonElement).click();
    await settle();
    startRequest().flush(
      { title: 'Conflict', status: 409, detail: 'x', audioRemainingCount: 1 },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    (
      document.querySelector('r2m-confirm-dialog .r2m-confirm-dialog__cancel') as HTMLButtonElement
    ).click();
    await settle(300);
    await fixture.whenStable();

    http.expectNone({ method: 'POST', url: '/api/projects/dune/assembly' });
  });

  it('deletes an output after a confirm and reloads the list', async () => {
    const { fixture, el } = await render();

    (el.querySelectorAll('[data-action="delete-output"]')[1] as HTMLButtonElement).click();
    await settle();
    (
      document.querySelector('r2m-confirm-dialog .r2m-confirm-dialog__confirm') as HTMLButtonElement
    ).click();

    (await eventually('DELETE', '/api/projects/dune/assembly/outputs/Dune.m4b')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await settle();
    http.expectOne('/api/projects/dune/assembly/outputs').flush([OUTPUTS[0]]);
    await settle();
    await fixture.whenStable();

    expect(el.querySelectorAll('[data-output]').length).toBe(1);
  });
});
