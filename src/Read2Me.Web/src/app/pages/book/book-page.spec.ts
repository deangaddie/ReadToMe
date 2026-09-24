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
import { AudioGenerator } from './audio-generator';
import { AudioSelectionStore } from './audio-selection-store';
import { BookEditor } from './book-editor';
import { BookPage } from './book-page';
import { Preflight } from '@app/shared/preflight';
import { BookStore } from './book-store';
import { SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';

const BASE = '/api/projects/dune';

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [{ id: 'h', name: 'Hardin', aliases: [] }],
  totalParts: 1,
  totalChapters: 2,
};

const NARRATION = (id: string) => ({
  id,
  isPauseParagraph: false,
  items: [
    {
      id: `${id}-i`,
      itemType: 'Narration' as const,
      text: id,
      characterId: '00000000-0000-0000-0000-000000000001',
      audioFileName: null,
      voiceInstructions: null,
      orderKey: 'a',
      isPause: false,
    },
  ],
});

const DIALOG = (id: string) => ({
  id,
  isPauseParagraph: false,
  items: [
    {
      id: `${id}-i`,
      itemType: 'Character' as const,
      text: `"${id}"`,
      characterId: null,
      audioFileName: null,
      voiceInstructions: null,
      orderKey: 'a',
      isPause: false,
    },
  ],
});

describe('BookPage', () => {
  let http: HttpTestingController;
  let toasts: string[];
  let assigner: { assign: ReturnType<typeof vi.fn>; createAndAssign: ReturnType<typeof vi.fn> };
  let generator: {
    enqueueItems: ReturnType<typeof vi.fn>;
    enqueueNode: ReturnType<typeof vi.fn>;
    working: ReturnType<typeof signal<boolean>>;
  };
  const queue = signal<{ attribution: { isBusy: boolean } } | null>(null);
  const detail = signal<{ narratorOnlyMode: boolean; narrator: null } | null>(null);

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  // jsdom has no Element.scrollTo; the CDK viewport calls it when the page scrolls to a chapter,
  // and the resulting unhandled error used to cascade through every later test in the file.
  beforeAll(() => {
    Element.prototype.scrollTo ??= () => undefined;
  });

  beforeEach(async () => {
    toasts = [];
    queue.set(null);
    detail.set(null);
    assigner = { assign: vi.fn().mockResolvedValue(undefined), createAndAssign: vi.fn() };
    generator = {
      enqueueItems: vi.fn().mockResolvedValue(true),
      enqueueNode: vi.fn().mockResolvedValue(true),
      working: signal(false),
    };
    await TestBed.configureTestingModule({
      imports: [BookPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        BookStore,
        { provide: Preflight, useValue: { ensureReady: () => Promise.resolve(true) } },
        BookEditor,
        SelectionStore,
        AudioSelectionStore,
        { provide: SpeakerAssigner, useValue: assigner },
        { provide: AudioGenerator, useValue: generator },
        {
          provide: ProjectStore,
          useValue: {
            folder: signal('dune'),
            revision: signal(1),
            status: signal({}),
            detail,
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
          useValue: { receipts$: () => new Subject(), resynced$: new Subject(), queue },
        },
        {
          provide: ToastService,
          useValue: {
            info: () => undefined,
            success: (m: string) => toasts.push(m),
            problem: (p: { detail?: string }) => toasts.push(`problem:${p.detail}`),
          },
        },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    TestBed.inject(BookStore).close();
    // The page polls the viewport edges every 250 ms and jsdom's zero-height viewport is always
    // "near the end", so a slow test can leave an edge load for the next chapter in flight. It is
    // timing noise, not behaviour under test: drop it (and audio mode's voices read that comes
    // with it) rather than let verify() fail on it.
    http.match((r) => /\/nodes\/chapter\/[^/]+\/(children|voices)$/.test(r.url));
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  async function render(mode?: string) {
    const fixture = TestBed.createComponent(BookPage);
    if (mode) fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    await settle();
    return fixture;
  }

  async function loadBook(paragraphs: unknown[] = []) {
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
    http.expectOne(`${BASE}/nodes/chapter/c1/children`).flush({ paragraphs });
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

  describe('selection (ticket 12)', () => {
    it('a tree checkbox reads the node paragraph ids, selects them and shows the action bar', async () => {
      const fixture = await render('speakers');
      await loadBook([DIALOG('p1'), DIALOG('p2')]);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('[data-testid=selection-bar]')).toBeNull();

      const box = el.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!;
      box.checked = true;
      box.dispatchEvent(new Event('change'));
      http.expectOne(`${BASE}/nodes/chapter/c1/paragraph-ids`).flush([
        { id: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' },
        { id: 'p2', chapterId: 'c1', partId: 'p1', volumeId: 'v1' },
      ]);
      await settle();
      fixture.detectChanges();

      const selection = TestBed.inject(SelectionStore);
      expect(selection.ids()).toEqual(['p1', 'p2']);
      expect(selection.nodeState('chapter', 'c1')).toBe('checked');
      expect(el.querySelector('[data-testid=selection-count]')?.textContent?.trim()).toBe(
        '2 paragraphs',
      );
      expect(box.checked).toBe(true);
      expect(
        Array.from(el.querySelectorAll<HTMLInputElement>('r2m-paragraph input[type=checkbox]')).map(
          (b) => b.checked,
        ),
      ).toEqual([true, true]);

      // Unticking reads again and removes exactly those.
      box.checked = false;
      box.dispatchEvent(new Event('change'));
      http
        .expectOne(`${BASE}/nodes/chapter/c1/paragraph-ids`)
        .flush([{ id: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' }]);
      await settle();
      expect(selection.ids()).toEqual(['p2']);
    });

    it('Attribute posts the selected ids, reports the count and clears the selection', async () => {
      const fixture = await render('speakers');
      await loadBook([DIALOG('p1')]);
      const selection = TestBed.inject(SelectionStore);
      selection.toggle('p1', { chapterId: 'c1', partId: 'p1', volumeId: 'v1' }, true);
      fixture.detectChanges();
      await fixture.whenStable();

      const el = fixture.nativeElement as HTMLElement;
      el.querySelector<HTMLButtonElement>('[data-action=attribute-selection]')!.click();
      await settle();
      const request = http.expectOne(`${BASE}/attribution/enqueue-paragraphs`);
      expect(request.request.body).toEqual({ paragraphIds: ['p1'] });
      request.flush({ enqueued: 1 }, { status: 202, statusText: 'Accepted' });
      await settle();

      expect(toasts).toEqual(['Queued 1 paragraph']);
      expect(selection.count()).toBe(0);
    });

    it('Bulk assign hands the selection to the assigner and is disarmed while attribution runs', async () => {
      const fixture = await render();
      await loadBook([DIALOG('p1')]);
      const selection = TestBed.inject(SelectionStore);
      selection.toggle('p1', { chapterId: 'c1', partId: 'p1', volumeId: 'v1' }, true);
      fixture.detectChanges();
      await fixture.whenStable();

      const el = fixture.nativeElement as HTMLElement;
      const bulk = el.querySelector<HTMLButtonElement>('[data-action=bulk-assign]')!;
      expect(bulk.disabled).toBe(false);
      bulk.click();
      fixture.detectChanges();
      document.querySelector<HTMLButtonElement>('.r2m-speaker-menu__row')!.click();
      expect(assigner.assign).toHaveBeenCalledWith(
        { kind: 'selection', paragraphIds: ['p1'] },
        'h',
      );

      queue.set({ attribution: { isBusy: true } });
      fixture.detectChanges();
      expect(bulk.disabled).toBe(true);
    });

    it('Audio mode hides the paragraph checkboxes and bar; the paragraph selection is kept aside', async () => {
      const fixture = await render('audio');
      await loadBook([DIALOG('p1')]);
      http.match(`${BASE}/nodes/chapter/c1/voices`).forEach((r) => r.flush({}));
      TestBed.inject(SelectionStore).toggle(
        'p1',
        { chapterId: 'c1', partId: null, volumeId: null },
        true,
      );
      fixture.detectChanges();
      await fixture.whenStable();

      const el = fixture.nativeElement as HTMLElement;
      expect(
        el.querySelector('r2m-paragraph > .r2m-paragraph__gutter input[type=checkbox]'),
      ).toBeNull();
      expect(el.querySelector('[data-testid=selection-bar]')).toBeNull();
      expect(TestBed.inject(SelectionStore).count()).toBe(1);
    });
  });

  describe('audio selection (ticket 13)', () => {
    async function renderAudio(paragraphs: unknown[]) {
      const fixture = await render('audio');
      await loadBook(paragraphs);
      http.match(`${BASE}/nodes/chapter/c1/voices`).forEach((r) => r.flush({}));
      fixture.detectChanges();
      await fixture.whenStable();
      return fixture;
    }

    it('a tree checkbox reads the node item ids, selects them and shows the item action bar', async () => {
      const fixture = await renderAudio([NARRATION('p1'), DIALOG('p2')]);
      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('[data-testid=selection-bar]')).toBeNull();

      const box = el.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!;
      box.checked = true;
      box.dispatchEvent(new Event('change'));
      http
        .expectOne(`${BASE}/nodes/chapter/c1/item-ids`)
        .flush([{ id: 'p1-i', paragraphId: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' }]);
      await settle();
      fixture.detectChanges();

      const selection = TestBed.inject(AudioSelectionStore);
      expect(selection.ids()).toEqual(['p1-i']);
      expect(selection.nodeState('chapter', 'c1')).toBe('checked');
      expect(box.checked).toBe(true);
      expect(el.querySelector('[data-testid=selection-count]')?.textContent?.trim()).toBe('1 item');
      const rows = Array.from(
        el.querySelectorAll<HTMLInputElement>('r2m-item input[type=checkbox]'),
      );
      expect(rows.map((b) => [b.checked, b.disabled])).toEqual([
        [true, false],
        [false, true],
      ]);
      expect(TestBed.inject(SelectionStore).count()).toBe(0);
    });

    it('Select needs audio reads with the filter and narrator-only flag; Generate audio queues and clears', async () => {
      detail.set({ narratorOnlyMode: true, narrator: null });
      const fixture = await renderAudio([NARRATION('p1')]);
      const el = fixture.nativeElement as HTMLElement;

      el.querySelector<HTMLButtonElement>('[data-node-id=c1] r2m-node-menu button')!.click();
      fixture.detectChanges();
      document.querySelector<HTMLButtonElement>('[data-entry=select-needs-audio]')!.click();
      http
        .expectOne(`${BASE}/nodes/chapter/c1/item-ids?needsAudioOnly=true&narratorOnlyMode=true`)
        .flush([{ id: 'p1-i', paragraphId: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' }]);
      await settle();
      fixture.detectChanges();

      const selection = TestBed.inject(AudioSelectionStore);
      expect(selection.ids()).toEqual(['p1-i']);
      el.querySelector<HTMLButtonElement>('[data-action=generate-audio-selection]')!.click();
      await settle();
      expect(generator.enqueueItems).toHaveBeenCalledWith(['p1-i']);
      expect(selection.count()).toBe(0);
    });

    it('Generate audio for this node hands the node to the generator', async () => {
      const fixture = await renderAudio([NARRATION('p1')]);
      const el = fixture.nativeElement as HTMLElement;
      el.querySelector<HTMLButtonElement>('[data-node-id=c1] r2m-node-menu button')!.click();
      fixture.detectChanges();
      document.querySelector<HTMLButtonElement>('[data-entry=generate-audio-node]')!.click();
      await settle();
      expect(generator.enqueueNode).toHaveBeenCalledWith('chapter', 'c1', false);
    });
  });
});
