import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { NewProjectDialog, NewProjectResult } from './new-project-dialog';

describe('NewProjectDialog', () => {
  let http: HttpTestingController;
  let ref: MatDialogRef<NewProjectDialog, NewProjectResult>;
  let closed: NewProjectResult | undefined;
  let closedCount: number;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    closed = undefined;
    closedCount = 0;
    ref = TestBed.inject(MatDialog).open(NewProjectDialog, { autoFocus: false });
    ref.afterClosed().subscribe((r) => {
      closed = r;
      closedCount++;
    });
  });

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  async function settle(ms = 0) {
    await new Promise((r) => setTimeout(r, ms));
  }

  /** afterClosed waits for the dialog's exit animation, so poll instead of counting ticks. */
  async function waitForClose() {
    for (let i = 0; i < 100 && closedCount === 0; i++) await settle(10);
  }

  function input(name: string): HTMLInputElement {
    return document.querySelector(`input[name="${name}"]`) as HTMLInputElement;
  }

  function type(name: string, value: string): void {
    const el = input(name);
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }

  function createButton(): HTMLButtonElement {
    return document.querySelector('.new-project__create') as HTMLButtonElement;
  }

  it('auto-fills the project title and enables Create only when everything is valid', async () => {
    await settle();
    expect(createButton().disabled).toBe(true);

    type('bookTitle', 'Dune');
    await settle();
    expect(input('title').value).toBe('Dune');
    expect(createButton().disabled).toBe(true);

    type('author', 'Frank Herbert');
    ref.componentInstance.onFiles([new File(['x'], 'dune.epub')]);
    await settle();
    expect(createButton().disabled).toBe(false);
    expect(document.querySelector('.new-project__file')?.textContent).toContain('dune.epub');
  });

  it('shows the rejection inline and keeps Create disabled', async () => {
    type('bookTitle', 'Dune');
    type('author', 'Frank Herbert');
    ref.componentInstance.onRejected([{ file: new File([''], 'dune.pdf'), reason: 'type' }]);
    await settle();

    expect(document.querySelector('.new-project__file')?.textContent).toContain(
      'dune.pdf is not an .epub or .txt file.',
    );
    expect(createButton().disabled).toBe(true);
  });

  it('posts the multipart form and closes with the new folder', async () => {
    type('bookTitle', 'Dune');
    type('title', 'Dune (audiobook)');
    type('author', 'Frank Herbert');
    ref.componentInstance.onFiles([new File(['x'], 'dune.epub')]);
    await settle();

    createButton().click();
    const req = http.expectOne({ method: 'POST', url: '/api/projects' });
    const form = req.request.body as FormData;
    expect(form.get('title')).toBe('Dune (audiobook)');
    expect(form.get('bookTitle')).toBe('Dune');
    expect(form.get('author')).toBe('Frank Herbert');
    expect((form.get('file') as File).name).toBe('dune.epub');
    req.flush({ folderName: 'Dune-audiobook' }, { status: 201, statusText: 'Created' });
    await settle();
    http.expectOne({ method: 'GET', url: '/api/projects' }).flush([]);
    await waitForClose();

    expect(closedCount).toBe(1);
    expect(closed).toBe('Dune-audiobook');
  });

  it('keeps the form open and shows the host problem when creation fails', async () => {
    type('bookTitle', 'Dune');
    type('author', 'Frank Herbert');
    ref.componentInstance.onFiles([new File(['x'], 'dune.epub')]);
    await settle();

    createButton().click();
    http
      .expectOne({ method: 'POST', url: '/api/projects' })
      .flush(
        { title: 'Unprocessable', status: 422, detail: "A project folder 'Dune' already exists." },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
    await settle();
    await settle();

    expect(closedCount).toBe(0);
    expect(document.querySelector('.new-project__submit-error')?.textContent).toContain(
      "A project folder 'Dune' already exists.",
    );
    expect(createButton().disabled).toBe(false);
  });
});
