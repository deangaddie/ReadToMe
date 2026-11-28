import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../../testing/fake-api';
import { settle } from '../../../testing/fake-navigation';
import { FakeTimers } from '../../../testing/fake-timers';
import { ERROR_ANNOUNCE_DELAY_MS } from '@app/ui/field';
import type { NewProjectDialog } from './new-project-dialog';
import { openNewProjectDialog } from './new-project-dialog';
import './new-project-dialog';

let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  api.on('GET', '/api/series', []);
});
let timers: FakeTimers | undefined;
afterEach(() => {
  timers?.restore();
  timers = undefined;
  delete Element.prototype.ariaNotify;
  document.body.replaceChildren();
});

async function open() {
  const result = openNewProjectDialog();
  await Promise.resolve();
  const dialog = document.querySelector<NewProjectDialog>('r2m-new-project-dialog')!;
  await dialog.rendered();
  const input = (name: string) => dialog.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
  const type = async (name: string, value: string) => {
    const el = input(name);
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    await dialog.rendered();
  };
  const createButton = () => dialog.querySelector<HTMLButtonElement>('.new-project__create')!;
  return { result, dialog, input, type, createButton };
}

describe('r2m-new-project-dialog', () => {
  it('auto-fills the project title, and Create is an enabled submit whatever the draft holds', async () => {
    const { dialog, input, type, createButton } = await open();
    expect(createButton().type).toBe('submit');
    expect(createButton().closest('form')).not.toBeNull();
    expect(dialog.querySelector<HTMLButtonElement>('.new-project__cancel')!.type).toBe('button');
    expect(createButton().disabled).toBe(false);

    await type('bookTitle', 'Dracula');
    expect(input('title').value).toBe('Dracula');
    expect(createButton().disabled).toBe(false);

    await type('author', 'Bram Stoker');
    dialog.onFiles([new File(['x'], 'dracula.epub')]);
    await dialog.rendered();
    expect(createButton().disabled).toBe(false);
    expect(dialog.querySelector('.new-project__file')?.textContent).toContain('dracula.epub');
  });

  it('autofocuses the book title, and only it', async () => {
    const { dialog } = await open();
    expect(
      Array.from(dialog.querySelectorAll('input[autofocus]'), (i) => i.getAttribute('name')),
    ).toEqual(['bookTitle']);
  });

  it('Create on an empty draft shows every error, the file one included, focuses Book title and sends nothing', async () => {
    const { dialog, input, createButton } = await open();
    expect(dialog.querySelectorAll('.r2m-field__error')).toHaveLength(0);

    createButton().click();
    await settle();
    await dialog.rendered();

    expect(Array.from(dialog.querySelectorAll('.r2m-field__error'), (e) => e.textContent)).toEqual([
      'Book title is required',
      'Project title is required',
      'Author is required',
    ]);
    expect(input('bookTitle').getAttribute('aria-invalid')).toBe('true');
    expect(dialog.querySelector('.new-project__file')?.textContent).toContain('Choose a book file');
    expect(document.activeElement).toBe(input('bookTitle'));
    expect(api.calls('POST', '/api/projects')).toHaveLength(0);
  });

  it('Create with every field but the file focuses the drop’s Choose file button', async () => {
    const { dialog, type, createButton } = await open();
    await type('bookTitle', 'Dracula');
    await type('author', 'Bram Stoker');

    createButton().click();
    await settle();
    await dialog.rendered();

    expect(dialog.querySelectorAll('.r2m-field__error')).toHaveLength(0);
    expect(dialog.querySelector('.new-project__file')?.textContent).toContain('Choose a book file');
    expect((document.activeElement as HTMLElement).textContent?.trim()).toBe('Choose file');
    expect(document.activeElement?.closest('r2m-file-drop')).not.toBeNull();
    expect(api.calls('POST', '/api/projects')).toHaveLength(0);
  });

  it('a field shows its error once blurred, not while typing, and clears it on input once fixed', async () => {
    const { dialog, input, type } = await open();
    await type('author', 'Bram');
    await type('author', '');
    expect(dialog.querySelector('.r2m-field__error')).toBeNull();

    input('author').dispatchEvent(new Event('blur'));
    await dialog.rendered();
    expect(dialog.querySelector('.r2m-field__error')?.textContent).toBe('Author is required');

    await type('author', 'B');
    expect(dialog.querySelector('.r2m-field__error')).toBeNull();
  });

  it('announces the error a blur brings up once, and none for a failed submit', async () => {
    const said: string[] = [];
    // happy-dom has no ariaNotify; the announcer calls it on the open modal.
    Element.prototype.ariaNotify = (text: string) => void said.push(text);
    timers = new FakeTimers().install();
    const { dialog, input, type, createButton } = await open();

    input('author').dispatchEvent(new Event('blur'));
    await dialog.rendered();
    const error = dialog.querySelector('.r2m-field__error')!;
    expect(error.hasAttribute('role')).toBe(false);
    expect(input('author').getAttribute('aria-invalid')).toBe('true');
    expect(input('author').getAttribute('aria-describedby')).toBe(error.id);
    expect(input('bookTitle').getAttribute('aria-invalid')).toBe('false');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toEqual(['Author is required']);

    input('author').focus();
    await type('author', 'B');
    await type('author', '');
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(said).toHaveLength(1);

    createButton().click();
    await settle();
    await dialog.rendered();
    await timers.advance(ERROR_ANNOUNCE_DELAY_MS);
    expect(dialog.querySelectorAll('.r2m-field__error')).toHaveLength(3);
    expect(said).toHaveLength(1);
  });

  it('shows the rejection inline and does not send it', async () => {
    const { dialog, type, createButton } = await open();
    await type('bookTitle', 'Dracula');
    await type('author', 'Bram Stoker');
    dialog.onRejected([{ file: new File([''], 'dracula.pdf'), reason: 'type' }]);
    await dialog.rendered();

    expect(dialog.querySelector('.new-project__file')?.textContent).toContain(
      'dracula.pdf is not an .epub or .txt file.',
    );
    createButton().click();
    await settle();
    expect(api.calls('POST', '/api/projects')).toHaveLength(0);
  });

  it('posts the multipart form and closes with the new folder', async () => {
    const { result, dialog, type, createButton } = await open();
    api
      .on('POST', '/api/projects', { folderName: 'Dracula-audiobook' })
      .on('GET', '/api/projects', []);
    await type('bookTitle', 'Dracula');
    await type('title', 'Dracula (audiobook)');
    await type('author', 'Bram Stoker');
    dialog.onFiles([new File(['x'], 'dracula.epub')]);
    await dialog.rendered();

    createButton().click();
    expect(await result).toBe('Dracula-audiobook');

    const [post] = api.calls('POST', '/api/projects');
    const form = post!.body as FormData;
    expect(form.get('title')).toBe('Dracula (audiobook)');
    expect(form.get('bookTitle')).toBe('Dracula');
    expect(form.get('author')).toBe('Bram Stoker');
    expect((form.get('file') as File).name).toBe('dracula.epub');
    expect(api.calls('GET', '/api/projects')).toHaveLength(1);
  });

  it('keeps the form open and shows the host problem when creation fails', async () => {
    const { dialog, type, createButton } = await open();
    api.on('POST', '/api/projects', () =>
      problem(422, "A project folder 'Dracula' already exists."),
    );
    await type('bookTitle', 'Dracula');
    await type('author', 'Bram Stoker');
    dialog.onFiles([new File(['x'], 'dracula.epub')]);
    await dialog.rendered();

    createButton().click();
    await settle();
    await dialog.rendered();

    expect(document.querySelector('r2m-new-project-dialog')).not.toBeNull();
    expect(dialog.querySelector('.new-project__submit-error')?.textContent).toContain(
      "A project folder 'Dracula' already exists.",
    );
    expect(createButton().disabled).toBe(false);
  });

  it('closes on Escape but not on a backdrop click', async () => {
    const { dialog } = await open();
    expect(dialog.closest('dialog')?.getAttribute('closedby')).toBe('closerequest');
  });

  it('cannot be closed while the upload is in flight, and closes on Escape otherwise', async () => {
    const { result, dialog, type, createButton } = await open();
    let finish: (r: Response) => void = () => {};
    api
      .on('POST', '/api/projects', () => new Promise<Response>((r) => (finish = r)))
      .on('GET', '/api/projects', []);
    await type('bookTitle', 'Dracula');
    await type('author', 'Bram Stoker');
    dialog.onFiles([new File(['x'], 'dracula.epub')]);
    await dialog.rendered();
    const host = dialog.closest('dialog')!;
    const escape = () => host.dispatchEvent(new Event('cancel', { cancelable: true }));

    createButton().click();
    await settle();
    expect(dialog.canClose()).toBe(false);
    await dialog.rendered();
    expect(createButton().disabled).toBe(true);
    escape();
    await settle();
    expect(host.open).toBe(true);

    finish(problem(422, 'No room'));
    await settle();
    escape();
    expect(await result).toBeUndefined();
  });

  it('Cancel closes with nothing', async () => {
    const { result, dialog } = await open();
    dialog.querySelector<HTMLButtonElement>('.new-project__cancel')!.click();
    expect(await result).toBeUndefined();
  });
});
