import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { PromptCatalogEntry } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { PREVIEW_DEBOUNCE_MS } from './prompt-editor';
import type { PromptsPage } from './prompts-page';
import './prompts-page';

/** Ported from the Angular TestBed spec, case for case, over `FakeApi`, `FakeLive` and a recorded confirm. */
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

let api: FakeApi;
let live: FakeLive;
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  override(ToastService, {
    success: () => undefined,
    info: () => undefined,
    problem: () => undefined,
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

async function flush(page: PromptsPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function render(catalog = [CHARACTER, VOICE]) {
  api.on('GET', CATALOG, catalog);
  const page = document.createElement('r2m-prompts-page') as PromptsPage;
  document.body.append(page);
  await flush(page);
  return page;
}

const textarea = (page: Element) =>
  page.querySelector<HTMLTextAreaElement>('[data-field="template"]')!;

async function type(page: PromptsPage, value: string) {
  const input = textarea(page);
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await page.rendered();
}

const button = (el: Element, label: string) =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

const row = (page: Element, kind: string) =>
  page.querySelector<HTMLButtonElement>(`[data-kind="${kind}"]`)!;

/** Opens the preview panel as a click on its summary would. */
async function openPreview(page: PromptsPage) {
  const details = page.querySelector<HTMLDetailsElement>('.prompt-editor__preview')!;
  details.open = true;
  details.dispatchEvent(new Event('toggle'));
  await page.rendered();
}

describe('r2m-prompts-page', () => {
  it('lists every kind with its override marker and warning, and opens the first', async () => {
    const page = await render();

    const rows = Array.from(page.querySelectorAll('.prompts__row'));
    expect(rows.map((r) => r.querySelector('.prompts__name')?.textContent?.trim())).toEqual([
      'Book Character Prompt',
      'Character Voice Prompt',
    ]);
    expect(rows[0]!.querySelector('.r2m-status-chip')).toBeNull();
    expect(rows[0]!.querySelector('[data-role="warning-icon"]')).toBeNull();
    expect(rows[1]!.querySelector('.r2m-status-chip')?.textContent).toContain('Overridden');
    expect(rows[1]!.querySelector('[data-role="warning-icon"]')).not.toBeNull();

    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
      'Book Character Prompt',
    );
    expect(textarea(page).value).toBe('DEFAULT {{book_title}}');
    expect(page.querySelector('[data-role="expected-response"]')?.textContent).toBe(
      '{ "items": [] }',
    );
    expect(
      Array.from(page.querySelectorAll('[data-token]')).map((b) => b.textContent?.trim()),
    ).toEqual(['{{book_title}}', '{{context_json}}', '{{narrator_identity}}']);
    expect(button(page, 'Save').disabled).toBe(true);
    expect(page.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.disabled).toBe(
      true,
    );
  });

  it('shows the warning and hides the expected response for an overridden plain-text kind', async () => {
    const page = await render();
    row(page, 'voice').click();
    await flush(page);

    expect(textarea(page).value).toBe('MINE {{character_name}}');
    expect(page.querySelector('[data-role="prompt-warning"]')?.textContent).toContain(
      'Stored override missing {{narrator_identity}}',
    );
    expect(page.querySelector('[data-role="expected-response"]')).toBeNull();
    expect(page.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.disabled).toBe(
      false,
    );
  });

  it('inserts a token at the caret, replacing the selection', async () => {
    const page = await render();
    const input = textarea(page);
    input.setSelectionRange(8, 22); // "{{book_title}}"
    page.querySelector<HTMLButtonElement>('[data-token="context_json"]')!.click();
    await flush(page);

    expect(input.value).toBe('DEFAULT {{context_json}}');
    expect(input.selectionStart).toBe(24);
    expect(page.hasUnsavedChanges()).toBe(true);
  });

  it('saves the draft and reloads the catalog', async () => {
    const page = await render();
    await type(page, 'DEFAULT {{book_title}} more');
    expect(page.querySelector('.r2m-config-editor-frame__dirty')).not.toBeNull();

    api.on('PUT', '/api/settings/prompts/character', undefined);
    const saved = entry({ template: 'DEFAULT {{book_title}} more', isOverridden: true });
    api.on('GET', CATALOG, [saved, VOICE]);
    button(page, 'Save').click();
    await flush(page);

    expect(api.calls('PUT', '/api/settings/prompts/character')[0]?.body).toEqual({
      template: 'DEFAULT {{book_title}} more',
    });
    expect(page.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    expect(page.querySelector('.prompts__row .r2m-status-chip')?.textContent).toContain(
      'Overridden',
    );
    expect(page.hasUnsavedChanges()).toBe(false);
  });

  it('resets a stored override after a confirm and shows the default again', async () => {
    const page = await render();
    row(page, 'voice').click();
    await flush(page);

    api.on('DELETE', '/api/settings/prompts/voice', undefined);
    api.on('GET', CATALOG, [
      CHARACTER,
      { ...VOICE, template: VOICE.defaultTemplate, isOverridden: false, warnings: [] },
    ]);
    page.querySelector<HTMLButtonElement>('[data-action="reset-prompt"]')!.click();
    await flush(page);

    expect(confirms[0]?.title).toBe('Reset Character Voice Prompt to default?');
    expect(api.calls('DELETE', '/api/settings/prompts/voice').length).toBe(1);
    expect(textarea(page).value).toBe('DEFAULT voice');
    expect(page.querySelector('[data-role="prompt-warning"]')).toBeNull();
  });

  it('previews the unsaved draft and follows further edits', async () => {
    const page = await render();
    await type(page, 'Hello {{book_title}}');
    const previews: string[] = [];
    api.on('POST', '/api/settings/prompts/character/preview', (body) => {
      const { template } = body as { template: string };
      previews.push(template);
      return { rendered: template.replace('{{book_title}}', 'Pride and Prejudice') };
    });

    await openPreview(page);
    expect(page.querySelector('.prompt-editor__preview .r2m-spinner')).not.toBeNull();
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    await flush(page);
    expect(previews).toEqual(['Hello {{book_title}}']);
    expect(page.querySelector('[data-role="preview"]')?.textContent).toBe(
      'Hello Pride and Prejudice',
    );

    await type(page, 'Bye {{book_title}}');
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    await flush(page);
    expect(previews).toEqual(['Hello {{book_title}}', 'Bye {{book_title}}']);
    expect(page.querySelector('[data-role="preview"]')?.textContent).toBe(
      'Bye Pride and Prejudice',
    );
  });

  it('a reload reaches an untouched editor without lighting it up as dirty', async () => {
    const page = await render();

    api.on('GET', CATALOG, [entry({ template: 'FROM ANOTHER TAB', isOverridden: true }), VOICE]);
    live.emit('settingsChanged', { area: 'prompts' });
    await flush(page);

    expect(textarea(page).value).toBe('FROM ANOTHER TAB');
    expect(page.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    expect(page.hasUnsavedChanges()).toBe(false);
  });

  it('reloads on settingsChanged for prompts without wiping a draft in progress', async () => {
    const page = await render();
    await type(page, 'typing…');

    live.emit('settingsChanged', { area: 'llm' });
    await flush(page);
    expect(api.calls('GET', CATALOG).length).toBe(1);

    api.on('GET', CATALOG, [entry({ template: 'FROM ANOTHER TAB', isOverridden: true }), VOICE]);
    live.emit('settingsChanged', { area: 'prompts' });
    await flush(page);

    expect(textarea(page).value).toBe('typing…');
    expect(page.querySelector('.prompts__row .r2m-status-chip')?.textContent).toContain(
      'Overridden',
    );

    button(page, 'Cancel').click();
    await flush(page);
    expect(textarea(page).value).toBe('FROM ANOTHER TAB');
  });

  it('stops listening while detached and listens again once re-attached', async () => {
    const page = await render();
    page.remove();
    live.emit('settingsChanged', { area: 'prompts' });
    await settle();
    expect(api.calls('GET', CATALOG).length).toBe(1);

    document.body.append(page);
    await flush(page);
    expect(api.calls('GET', CATALOG).length).toBe(2);
    live.emit('settingsChanged', { area: 'prompts' });
    await flush(page);
    expect(api.calls('GET', CATALOG).length).toBe(3);
  });

  it('asks before switching kinds with unsaved edits', async () => {
    const page = await render();
    await type(page, 'edited');

    confirmAnswer = false;
    row(page, 'voice').click();
    await flush(page);
    expect(confirms[0]?.message).toContain('unsaved changes');

    expect(textarea(page).value).toBe('edited');
    expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
      'Book Character Prompt',
    );
  });
});
