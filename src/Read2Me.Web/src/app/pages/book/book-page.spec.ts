import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { BookOverviewDto } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { Subject } from 'rxjs';
import { ProjectStore } from '../project/project-store';
import { BookPage } from './book-page';
import { BookStore } from './book-store';

const BASE = '/api/projects/dune';

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [],
  totalParts: 1,
  totalChapters: 2,
};

describe('BookPage', () => {
  let http: HttpTestingController;

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        BookStore,
        {
          provide: ProjectStore,
          useValue: {
            folder: signal('dune'),
            revision: signal(1),
            status: signal({}),
            detail: signal(null),
            nodes: signal({
              c1: {
                attributionRemaining: 3,
                audioRemaining: 0,
                review: 0,
                attributionProcessing: false,
                attributionQueued: 0,
                isDone: false,
              },
            }),
            paragraphs: signal({}),
            items: signal({}),
          },
        },
        {
          provide: LiveService,
          useValue: { receipts$: () => new Subject(), resynced$: new Subject() },
        },
        { provide: ToastService, useValue: { info: () => undefined } },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    TestBed.inject(BookStore).close();
    http.verify();
  });

  async function render(mode?: string) {
    const fixture = TestBed.createComponent(BookPage);
    if (mode) fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    await settle();
    return fixture;
  }

  async function loadBook() {
    http.expectOne(`${BASE}/book`).flush(OVERVIEW);
    http.expectOne(`${BASE}/audio/reviews`).flush({});
    await settle();
    http
      .expectOne(`${BASE}/nodes/volume/v1/children`)
      .flush({ parts: [{ id: 'p1', title: null }] });
    await settle();
    http.expectOne(`${BASE}/nodes/part/p1/children`).flush({
      chapters: [
        { id: 'c1', title: 'The Encyclopedists' },
        { id: 'c2', title: null },
      ],
    });
    await settle();
    http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush({ paragraphs: [] });
    await settle();
  }

  it('shows an empty state when the book has not been read in', async () => {
    const fixture = await render();
    http.expectOne(`${BASE}/book`).flush({ ...OVERVIEW, hasContent: false, volumes: [] });
    http.expectOne(`${BASE}/audio/reviews`).flush({});
    await settle();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('The book has not been read in yet');
    expect(fixture.nativeElement.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
  });

  it('opens the first chapter and lists chapters in the tree with their badges', async () => {
    const fixture = await render();
    await loadBook();
    fixture.detectChanges();
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    const titles = Array.from(el.querySelectorAll('.tree__title')).map((n) =>
      n.textContent?.trim(),
    );
    expect(titles).toEqual(['The Encyclopedists', 'Chapter 2']);
    expect(el.querySelector('[data-node-id=c1] r2m-count-badge')?.textContent).toContain('3');
    expect(TestBed.inject(BookStore).window()).toEqual(['c1']);
  });

  it('?mode= sets the reader mode; switching mode mirrors it to the URL', async () => {
    const fixture = await render('audio');
    const store = TestBed.inject(BookStore);
    expect(store.mode()).toBe('audio');
    await loadBook();
    // Audio mode reads voices with the chapter.
    http.match(`${BASE}/nodes/chapter/c1/voices`).forEach((r) => r.flush({}));

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    const toggles = fixture.nativeElement.querySelectorAll('mat-button-toggle button');
    (toggles[1] as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(store.mode()).toBe('speakers');
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { mode: 'speakers' }, queryParamsHandling: 'merge' }),
    );
  });
});
