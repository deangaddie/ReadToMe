import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../../testing/fake-api';
import { settle } from '../../../testing/fake-navigation';
import type { MergeDialog } from './merge-dialog';
import { openMergeDialog } from './merge-dialog';
import './merge-dialog';

function row(name: string, voiceCount = 0, isNarrator = false): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount,
    readyVoiceCount: 0,
    isNarrator,
    narratesBook: false,
  };
}

const ROWS = [row('Narrator', 0, true), row('Alice'), row('Bob'), row('Carol')];

let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
});
afterEach(() => document.body.replaceChildren());

async function open(merged: CharacterSummaryDto, rows = ROWS) {
  const result = openMergeDialog({ folder: 'dune', merged, rows });
  await Promise.resolve();
  const dialog = document.querySelector<MergeDialog>('r2m-merge-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

const options = (dialog: MergeDialog) =>
  Array.from(dialog.querySelectorAll<HTMLOptionElement>('option')).map((o) =>
    o.textContent?.trim(),
  );

describe('r2m-merge-dialog', () => {
  it('offers every other character but the Narrator, the first preselected, alias on', async () => {
    const { dialog } = await open(row('Bob'));
    expect(options(dialog)).toEqual(['Alice', 'Carol']);
    expect(dialog.querySelector<HTMLSelectElement>('select')?.value).toBe('alice');
    expect(dialog.querySelector<HTMLInputElement>('input[name=addNameAsAlias]')?.checked).toBe(
      true,
    );
    expect(dialog.textContent).toContain('Add "Bob" as an alias of the survivor');
    expect(dialog.querySelector<HTMLButtonElement>('.merge__submit')?.disabled).toBe(false);
    expect(dialog.querySelector('.merge__warning')).toBeNull();
  });

  it('closes with the survivor and the alias choice', async () => {
    const { result, dialog } = await open(row('Bob'));
    const select = dialog.querySelector<HTMLSelectElement>('select')!;
    select.value = 'carol';
    select.dispatchEvent(new Event('change'));
    const alias = dialog.querySelector<HTMLInputElement>('input[name=addNameAsAlias]')!;
    alias.checked = false;
    alias.dispatchEvent(new Event('change'));
    await dialog.rendered();
    dialog.querySelector<HTMLButtonElement>('.merge__submit')!.click();
    expect(await result).toEqual({ survivorId: 'carol', addNameAsAlias: false });
  });

  it('Cancel closes with null', async () => {
    const { result, dialog } = await open(row('Bob'));
    dialog.querySelector<HTMLButtonElement>('.merge__cancel')!.click();
    expect(await result).toBeNull();
  });

  it('without a candidate the form cannot be submitted', async () => {
    const { dialog } = await open(row('Alice'), [row('Narrator', 0, true), row('Alice')]);
    expect(options(dialog)).toEqual([]);
    expect(dialog.querySelector('.merge__error')?.textContent).toContain(
      'Select a character to merge into.',
    );
    expect(dialog.querySelector<HTMLButtonElement>('.merge__submit')?.disabled).toBe(true);
  });

  it('names the voices the merged character takes with it', async () => {
    api.on('GET', '/api/projects/dune/characters/bob/voices', {
      defaultVoiceId: null,
      voices: [{ name: 'Gruff' }, { name: 'Soft' }],
    });
    const { dialog } = await open(row('Bob', 2));
    await settle();
    await dialog.rendered();
    const warning = dialog.querySelector('.merge__warning')!;
    expect(warning.getAttribute('role')).toBe('alert');
    expect(warning.textContent).toContain('its 2 voices go with it');
    expect(warning.textContent).toContain('Gruff, Soft');
  });

  it('counts the voices when the list cannot be read', async () => {
    api.on('GET', '/api/projects/dune/characters/bob/voices', () => problem(500, 'down'));
    const { dialog } = await open(row('Bob', 1));
    await settle();
    await dialog.rendered();
    expect(dialog.querySelector('.merge__warning')?.textContent).toContain(
      'its voice goes with it',
    );
    expect(dialog.querySelector('.merge__warning')?.textContent).toContain('(voice)');
  });
});
