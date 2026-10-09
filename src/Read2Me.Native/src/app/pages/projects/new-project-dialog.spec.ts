import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../../testing/fake-api';
import { settle } from '../../../testing/fake-navigation';
import type { NewProjectDialog } from './new-project-dialog';
import { openNewProjectDialog } from './new-project-dialog';
import './new-project-dialog';

let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
});
afterEach(() => document.body.replaceChildren());

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
  it('auto-fills the project title and enables Create only when everything is valid', async () => {
    const { dialog, input, type, createButton } = await open();
    expect(createButton().disabled).toBe(true);

    await type('bookTitle', 'Dune');
    expect(input('title').value).toBe('Dune');
    expect(createButton().disabled).toBe(true);

    await type('author', 'Frank Herbert');
    dialog.onFiles([new File(['x'], 'dune.epub')]);
    await dialog.rendered();
    expect(createButton().disabled).toBe(false);
    expect(dialog.querySelector('.new-project__file')?.textContent).toContain('dune.epub');
  });

  it('shows the rejection inline and keeps Create disabled', async () => {
    const { dialog, type, createButton } = await open();
    await type('bookTitle', 'Dune');
    await type('author', 'Frank Herbert');
    dialog.onRejected([{ file: new File([''], 'dune.pdf'), reason: 'type' }]);
    await dialog.rendered();

    expect(dialog.querySelector('.new-project__file')?.textContent).toContain(
      'dune.pdf is not an .epub or .txt file.',
    );
    expect(createButton().disabled).toBe(true);
  });

  it('posts the multipart form and closes with the new folder', async () => {
    const { result, dialog, type, createButton } = await open();
    api.on('POST', '/api/projects', { folderName: 'Dune-audiobook' }).on('GET', '/api/projects', []);
    await type('bookTitle', 'Dune');
    await type('title', 'Dune (audiobook)');
    await type('author', 'Frank Herbert');
    dialog.onFiles([new File(['x'], 'dune.epub')]);
    await dialog.rendered();

    createButton().click();
    expect(await result).toBe('Dune-audiobook');

    const [post] = api.calls('POST', '/api/projects');
    const form = post!.body as FormData;
    expect(form.get('title')).toBe('Dune (audiobook)');
    expect(form.get('bookTitle')).toBe('Dune');
    expect(form.get('author')).toBe('Frank Herbert');
    expect((form.get('file') as File).name).toBe('dune.epub');
    expect(api.calls('GET', '/api/projects')).toHaveLength(1);
  });

  it('keeps the form open and shows the host problem when creation fails', async () => {
    const { dialog, type, createButton } = await open();
    api.on('POST', '/api/projects', () => problem(422, "A project folder 'Dune' already exists."));
    await type('bookTitle', 'Dune');
    await type('author', 'Frank Herbert');
    dialog.onFiles([new File(['x'], 'dune.epub')]);
    await dialog.rendered();

    createButton().click();
    await settle();
    await dialog.rendered();

    expect(document.querySelector('r2m-new-project-dialog')).not.toBeNull();
    expect(dialog.querySelector('.new-project__submit-error')?.textContent).toContain(
      "A project folder 'Dune' already exists.",
    );
    expect(createButton().disabled).toBe(false);
  });

  it('closes on Escape but not on a backdrop click', async () => {
    const { dialog } = await open();
    expect(dialog.closest('dialog')?.getAttribute('closedby')).toBe('closerequest');
  });

  it('Cancel closes with nothing', async () => {
    const { result, dialog } = await open();
    dialog.querySelector<HTMLButtonElement>('.new-project__cancel')!.click();
    expect(await result).toBeUndefined();
  });
});
