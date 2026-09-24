import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PromptCatalogEntry } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { SettingsChangedMessage } from '@app/live/live-messages';
import { Subject } from 'rxjs';
import { PREVIEW_DEBOUNCE_MS } from './prompt-editor';
import { PromptsPage } from './prompts-page';

class FakeLive {
  readonly settingsChanged = new Subject<SettingsChangedMessage>();
  on(family: string) {
    if (family !== 'settingsChanged') throw new Error(`unexpected family ${family}`);
    return this.settingsChanged.asObservable();
  }
}

const CATALOG = '/api/settings/prompts/catalog';

function entry(overrides: Partial<PromptCatalogEntry> = {}): PromptCatalogEntry {
  return {
    kind: 'character',
    title: 'Book Character Prompt',
    description: 'Who speaks each item.',
    tokens: ['book_title', 'context_json', 'narrator_identity'],
    expectedResponse: '{ "items": [] }',
    template: 'DEFAULT {{book_title}}',
    defaultTemplate: 'DEFAULT {{book_title}}',
    isOverridden: false,
    warnings: [],
    ...overrides,
  };
}

const CHARACTER = entry();
const VOICE = entry({
  kind: 'voice',
  title: 'Character Voice Prompt',
  tokens: ['book_title', 'character_name'],
  expectedResponse: null,
  template: 'MINE {{character_name}}',
  defaultTemplate: 'DEFAULT voice',
  isOverridden: true,
  warnings: ['Stored override missing {{narrator_identity}}'],
});

describe('PromptsPage', () => {
  let http: HttpTestingController;
  let live: FakeLive;

  beforeEach(async () => {
    live = new FakeLive();
    await TestBed.configureTestingModule({
      imports: [PromptsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: LiveService, useValue: live },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function eventually(method: string, url: string): Promise<TestRequest> {
    for (let i = 0; i < 100; i++) {
      const [match] = http.match((r) => r.method === method && r.urlWithParams === url);
      if (match) return match;
      await settle(10);
    }
    throw new Error(`No ${method} ${url}`);
  }

  async function stable(fixture: ComponentFixture<unknown>) {
    await settle();
    await fixture.whenStable();
  }

  async function render(catalog = [CHARACTER, VOICE]) {
    const fixture = TestBed.createComponent(PromptsPage);
    (await eventually('GET', CATALOG)).flush(catalog);
    await stable(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const textarea = (el: HTMLElement) =>
    el.querySelector<HTMLTextAreaElement>('[data-field="template"]')!;

  function type(el: HTMLElement, value: string) {
    const input = textarea(el);
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  const button = (el: HTMLElement, label: string) =>
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

  const row = (el: HTMLElement, kind: string) =>
    el.querySelector<HTMLButtonElement>(`[data-kind="${kind}"]`)!;

  it('lists every kind with its override marker and warning, and opens the first', async () => {
    const { el } = await render();

    const rows = Array.from(el.querySelectorAll('.prompts__row'));
    expect(rows.map((r) => r.querySelector('.prompts__name')?.textContent?.trim())).toEqual([
      'Book Character Prompt',
      'Character Voice Prompt',
    ]);
    expect(rows[0]!.querySelector('r2m-status-chip')).toBeNull();
    expect(rows[0]!.querySelector('[data-role="warning-icon"]')).toBeNull();
    expect(rows[1]!.querySelector('r2m-status-chip')?.textContent).toContain('Overridden');
    expect(rows[1]!.querySelector('[data-role="warning-icon"]')).not.toBeNull();

    expect(el.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
      'Book Character Prompt',
    );
    expect(textarea(el).value).toBe('DEFAULT {{book_title}}');
    expect(el.querySelector('[data-role="expected-response"]')?.textContent).toBe(
      '{ "items": [] }',
    );
    expect(
      Array.from(el.querySelectorAll('[data-token]')).map((b) => b.textContent?.trim()),
    ).toEqual(['{{book_title}}', '{{context_json}}', '{{narrator_identity}}']);
    expect(button(el, 'Save').disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.disabled).toBe(
      true,
    );
  });

  it('shows the warning and hides the expected response for an overridden plain-text kind', async () => {
    const { fixture, el } = await render();
    row(el, 'voice').click();
    await stable(fixture);

    expect(textarea(el).value).toBe('MINE {{character_name}}');
    expect(el.querySelector('[data-role="prompt-warning"]')?.textContent).toContain(
      'Stored override missing {{narrator_identity}}',
    );
    expect(el.querySelector('[data-role="expected-response"]')).toBeNull();
    expect(el.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.disabled).toBe(
      false,
    );
  });

  it('inserts a token at the caret, replacing the selection', async () => {
    const { fixture, el } = await render();
    const input = textarea(el);
    input.setSelectionRange(8, 22); // "{{book_title}}"
    el.querySelector<HTMLButtonElement>('[data-token="context_json"]')!.click();
    await stable(fixture);

    expect(input.value).toBe('DEFAULT {{context_json}}');
    expect(input.selectionStart).toBe(24);
    expect(fixture.componentInstance.hasUnsavedChanges()).toBe(true);
  });

  it('saves the draft and reloads the catalog', async () => {
    const { fixture, el } = await render();
    type(el, 'DEFAULT {{book_title}} more');
    await stable(fixture);
    expect(el.querySelector('.r2m-config-editor-frame__dirty')).not.toBeNull();

    button(el, 'Save').click();
    const put = await eventually('PUT', '/api/settings/prompts/character');
    expect(put.request.body).toEqual({ template: 'DEFAULT {{book_title}} more' });
    put.flush(null);
    const saved = entry({ template: 'DEFAULT {{book_title}} more', isOverridden: true });
    (await eventually('GET', CATALOG)).flush([saved, VOICE]);
    await stable(fixture);

    expect(el.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    expect(el.querySelector('.prompts__row r2m-status-chip')?.textContent).toContain('Overridden');
    expect(fixture.componentInstance.hasUnsavedChanges()).toBe(false);
  });

  it('resets a stored override after a confirm and shows the default again', async () => {
    const { fixture, el } = await render();
    row(el, 'voice').click();
    await stable(fixture);

    el.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.click();
    await stable(fixture);
    const dialog = document.querySelector('r2m-confirm-dialog') as HTMLElement;
    expect(dialog.textContent).toContain('Reset Character Voice Prompt to default?');
    button(dialog, 'Reset').click();
    (await eventually('DELETE', '/api/settings/prompts/voice')).flush(null);
    (await eventually('GET', CATALOG)).flush([
      CHARACTER,
      { ...VOICE, template: VOICE.defaultTemplate, isOverridden: false, warnings: [] },
    ]);
    await settle(300);
    await stable(fixture);

    expect(textarea(el).value).toBe('DEFAULT voice');
    expect(el.querySelector('[data-role="prompt-warning"]')).toBeNull();
  });

  it('previews the unsaved draft and follows further edits', async () => {
    const { fixture, el } = await render();
    type(el, 'Hello {{book_title}}');
    await stable(fixture);

    el.querySelector<HTMLElement>('mat-expansion-panel-header')!.click();
    await settle(PREVIEW_DEBOUNCE_MS + 50);
    const first = await eventually('POST', '/api/settings/prompts/character/preview');
    expect(first.request.body).toEqual({ template: 'Hello {{book_title}}' });
    first.flush({ rendered: 'Hello The Hobbit' });
    await stable(fixture);
    expect(el.querySelector('[data-role="preview"]')?.textContent).toBe('Hello The Hobbit');

    type(el, 'Bye {{book_title}}');
    await settle(PREVIEW_DEBOUNCE_MS + 50);
    const second = await eventually('POST', '/api/settings/prompts/character/preview');
    expect(second.request.body).toEqual({ template: 'Bye {{book_title}}' });
    second.flush({ rendered: 'Bye The Hobbit' });
    await stable(fixture);
    expect(el.querySelector('[data-role="preview"]')?.textContent).toBe('Bye The Hobbit');
  });

  it('a reload reaches an untouched editor without lighting it up as dirty', async () => {
    const { fixture, el } = await render();

    live.settingsChanged.next({ area: 'prompts' });
    (await eventually('GET', CATALOG)).flush([
      entry({ template: 'FROM BLAZOR', isOverridden: true }),
      VOICE,
    ]);
    await stable(fixture);

    expect(textarea(el).value).toBe('FROM BLAZOR');
    expect(el.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    expect(fixture.componentInstance.hasUnsavedChanges()).toBe(false);
  });

  it('reloads on settingsChanged for prompts without wiping a draft in progress', async () => {
    const { fixture, el } = await render();
    type(el, 'typing…');
    await stable(fixture);

    live.settingsChanged.next({ area: 'llm' });
    await settle(20);
    expect(http.match((r) => r.url === CATALOG)).toEqual([]);

    live.settingsChanged.next({ area: 'prompts' });
    (await eventually('GET', CATALOG)).flush([
      entry({ template: 'FROM BLAZOR', isOverridden: true }),
      VOICE,
    ]);
    await stable(fixture);

    expect(textarea(el).value).toBe('typing…');
    expect(el.querySelector('.prompts__row r2m-status-chip')?.textContent).toContain('Overridden');

    button(el, 'Cancel').click();
    await stable(fixture);
    expect(textarea(el).value).toBe('FROM BLAZOR');
  });

  it('asks before switching kinds with unsaved edits', async () => {
    const { fixture, el } = await render();
    type(el, 'edited');
    await stable(fixture);

    row(el, 'voice').click();
    await stable(fixture);
    const dialog = document.querySelector('r2m-confirm-dialog') as HTMLElement;
    expect(dialog.textContent).toContain('unsaved changes');
    button(dialog, 'Cancel').click();
    await settle(300);
    await stable(fixture);

    expect(textarea(el).value).toBe('edited');
    expect(el.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
      'Book Character Prompt',
    );
  });
});
