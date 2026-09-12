import { TestBed } from '@angular/core/testing';
import { provideRouter, withRouterConfig } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '@app/app.routes';
import { RailState } from './rail-state';
import { Shell } from './shell';

/** Every design §4 path, the rail group it should show, and the breadcrumb it should render. */
const CASES: { path: string; context: string[]; crumbs: string[]; title: string }[] = [
  { path: '/projects', context: [], crumbs: ['Projects'], title: 'Projects' },
  {
    path: '/projects/foundation',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation'],
    title: '',
  },
  {
    path: '/projects/foundation/book',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Book'],
    title: 'Book',
  },
  {
    path: '/projects/foundation/cast',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Cast'],
    title: 'Cast',
  },
  {
    path: '/projects/foundation/cast/abc',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Cast'],
    title: 'Cast',
  },
  {
    path: '/projects/foundation/voices/v1/editor',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Voice editor'],
    title: 'Voice editor',
  },
  {
    path: '/projects/foundation/export',
    context: ['Overview', 'Book', 'Cast', 'Export'],
    crumbs: ['Projects', 'foundation', 'Export'],
    title: 'Export',
  },
  ...[
    'llm',
    'prompts',
    'tts',
    'voice-design',
    'transcription',
    'similarity',
    'audio',
    'services',
    'themes',
  ].map((page) => ({
    path: `/settings/${page}`,
    context: [
      'LLM',
      'Prompts',
      'Paragraph TTS',
      'Voice design',
      'Transcription',
      'Similarity',
      'Audio processing',
      'AI services',
      'Themes',
    ],
    crumbs: ['Settings', ''],
    title: '',
  })),
  { path: '/styleguide', context: [], crumbs: ['Style guide'], title: 'Style guide' },
];

describe('Shell', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  async function mount(path: string) {
    await TestBed.configureTestingModule({
      imports: [Shell],
      providers: [provideRouter(routes, withRouterConfig({ paramsInheritanceStrategy: 'always' }))],
    }).compileComponents();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(path);
    const fixture = TestBed.createComponent(Shell);
    await fixture.whenStable();
    return fixture;
  }

  it.each(CASES)('resolves $path with the right rail group and breadcrumb', async (c) => {
    const fixture = await mount(c.path);
    const el = fixture.nativeElement as HTMLElement;

    const contextLabels = Array.from(
      el.querySelectorAll('.shell__nav-group--context .shell__nav-label'),
    ).map((n) => n.textContent?.trim());
    expect(contextLabels).toEqual(c.context);

    const crumbs = Array.from(el.querySelectorAll('.shell__crumb')).map((n) =>
      n.textContent?.trim(),
    );
    expect(crumbs.length).toBe(c.crumbs.length);
    c.crumbs.forEach((expected, i) => {
      if (expected) expect(crumbs[i]).toBe(expected);
    });
    if (c.title) expect(crumbs.at(-1)).toBe(c.title);
  });

  it('remembers the rail state across instances via localStorage', async () => {
    const fixture = await mount('/projects');
    expect(fixture.componentInstance.railExpanded()).toBe(true);

    fixture.componentInstance.toggleRail();
    expect(fixture.componentInstance.railExpanded()).toBe(false);
    expect(localStorage.getItem('r2m.rail.expanded')).toBe('0');

    // A fresh RailState (new "page load") reads the persisted value back.
    expect(new RailState().expanded()).toBe(false);
  });

  it('starts with the activity drawer closed and toggles it', async () => {
    const fixture = await mount('/projects');
    expect(fixture.componentInstance.drawerOpen()).toBe(false);
    fixture.componentInstance.toggleDrawer();
    expect(fixture.componentInstance.drawerOpen()).toBe(true);
  });
});
