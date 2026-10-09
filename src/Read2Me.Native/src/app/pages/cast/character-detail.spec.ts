import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterSummaryDto } from '@app/api';
import { Router } from '@app/core/router';
import { override, provide, resetServices } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import { CastStore } from './cast-store';
import { linkedNarratorDeleteMessage } from './character-detail';
import type { MergeDialog } from './merge-dialog';
import './character-detail';

const BASE = '/api/projects/dune';
const COMMANDS = `${BASE}/commands`;

function row(name: string, overrides: Partial<CharacterSummaryDto> = {}): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount: 0,
    readyVoiceCount: 0,
    isNarrator: name === 'Narrator',
    narratesBook: false,
    ...overrides,
  };
}

const ALICE = row('Alice', { aliases: [{ id: 'a1', name: 'Al' }], lineCount: 1 });
const ROWS = [row('Narrator'), ALICE, row('Bob')];

let api: FakeApi;
let navigation: FakeNavigation;
let toasts: string[];
let store: CastStore;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  override(LiveService, new FakeLive() as unknown as LiveService);
  navigation = installNavigation('projects/dune/cast/alice');
  const router = new Router();
  override(Router, router);
  router.start(
    [{ path: 'projects/:folder/cast/:characterId', tag: 'x-nothing' }],
    async () => true,
  );
  toasts = [];
  override(ToastService, {
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
  store = new CastStore();
});
afterEach(() => {
  store.close();
  document.body.replaceChildren();
});

/** The detail under a host that provides the store, as the cast page does. */
async function mount(character: CharacterSummaryDto, rows = ROWS) {
  api.on('GET', `${BASE}/characters/summary`, rows);
  api.on('GET', `${BASE}/characters/${character.id}/lines`, [
    { itemId: 'i1', paragraphId: 'p1', chapterId: 'c7', text: 'Hello there.' },
  ]);
  api.on('GET', `${BASE}/characters/${character.id}/voices`, { defaultVoiceId: null, voices: [] });
  api.on('GET', `${BASE}/characters/${character.id}/voice-rules`, []);
  api.on('GET', `${BASE}/characters/${character.id}/voice-rules/preview`, []);
  await store.open('dune');
  await store.select(character.id);
  const host = document.createElement('div');
  provide(host, CastStore, store);
  const detail = document.createElement('r2m-character-detail');
  detail.character = store.selected();
  host.append(detail);
  document.body.append(host);
  await detail.rendered();
  await detail.rendered();
  return detail;
}

const button = (el: Element, action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
const commands = () => api.calls('POST', COMMANDS).map((r) => r.body);

describe('r2m-character-detail', () => {
  it('shows the name, aliases and lines; the Narrator gets neither edit nor aliases', async () => {
    const detail = await mount(ALICE);
    expect(detail.querySelector('r2m-inline-edit')).not.toBeNull();
    expect(detail.querySelector('.character-detail__alias[data-alias="Al"]')).not.toBeNull();
    expect(detail.querySelector('.character-detail__count')?.textContent).toBe('1');
    expect(detail.querySelector('r2m-character-lines')?.textContent).toContain('Hello there.');
    // The Voices and Voice rules sections sit between the aliases and the lines (16, 17).
    expect(detail.querySelector('r2m-voices-section')?.character).toEqual(ALICE);
    expect(detail.querySelector('r2m-voices-section')?.textContent).toContain('No voices yet');
    expect(detail.querySelector('r2m-voice-rules-section')?.character).toEqual(ALICE);
    expect(button(detail, 'merge').disabled).toBe(false);

    document.body.replaceChildren();
    const narrator = await mount(row('Narrator'));
    expect(narrator.querySelector('r2m-inline-edit')).toBeNull();
    expect(narrator.querySelector('h2')?.textContent).toBe('Narrator');
    expect(narrator.querySelector('[aria-label="Aliases"]')).toBeNull();
    expect(button(narrator, 'delete')).toBeNull();
  });

  it('flags the character that narrates the book', async () => {
    const detail = await mount(row('Watson', { narratesBook: true }), [
      row('Narrator'),
      row('Watson', { narratesBook: true }),
    ]);
    expect(detail.querySelector('.r2m-status-chip')?.textContent).toContain('Narrates this book');
    // Watson is the only non-narrator: nothing to merge into.
    expect(button(detail, 'merge').disabled).toBe(true);
  });

  it('renaming posts RenameCharacter and the roster reload shows the new name', async () => {
    const detail = await mount(ALICE);
    api.on('POST', COMMANDS, { outcome: 'Committed' });
    api.on('GET', `${BASE}/characters/summary`, [
      row('Narrator'),
      { ...ALICE, name: 'Alice Liddell' },
    ]);
    detail.querySelector<HTMLButtonElement>('.r2m-inline-edit__display')!.click();
    await detail.rendered();
    const input = detail.querySelector<HTMLInputElement>('.r2m-inline-edit__input')!;
    input.value = 'Alice Liddell';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();
    expect(commands()).toEqual([
      { type: 'RenameCharacter', characterId: 'alice', name: 'Alice Liddell' },
    ]);
    expect(store.selected()?.name).toBe('Alice Liddell');
  });

  it('adds an alias on Enter, ignores the blur that follows, and removes one', async () => {
    const detail = await mount(ALICE);
    api.on('POST', COMMANDS, { outcome: 'Committed' });
    button(detail, 'add-alias').click();
    await detail.rendered();
    const input = detail.querySelector<HTMLInputElement>('.character-detail__alias-input')!;
    expect(document.activeElement).toBe(input);
    input.value = ' Alicia ';
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    input.dispatchEvent(new Event('blur'));
    await settle();
    expect(commands()).toEqual([
      { type: 'AddCharacterAlias', characterId: 'alice', name: 'Alicia' },
    ]);
    await detail.rendered();
    expect(detail.querySelector('.character-detail__alias-input')).toBeNull();

    detail.querySelector<HTMLButtonElement>('[aria-label="Remove alias Al"]')!.click();
    await settle();
    expect(commands().at(-1)).toEqual({ type: 'RemoveCharacterAlias', aliasId: 'a1' });
  });

  it('Escape or an empty alias closes the input without a command', async () => {
    const detail = await mount(ALICE);
    button(detail, 'add-alias').click();
    await detail.rendered();
    const input = detail.querySelector<HTMLInputElement>('.character-detail__alias-input')!;
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await detail.rendered();
    expect(detail.querySelector('.character-detail__alias-input')).toBeNull();

    button(detail, 'add-alias').click();
    await detail.rendered();
    detail
      .querySelector<HTMLInputElement>('.character-detail__alias-input')!
      .dispatchEvent(new Event('blur'));
    await settle();
    expect(commands()).toEqual([]);
  });

  it('Merge opens the dialog, posts MergeCharacters and goes to the survivor', async () => {
    const detail = await mount(ALICE);
    api.on('POST', COMMANDS, { outcome: 'Committed' });
    button(detail, 'merge').click();
    await settle();
    const dialog = document.querySelector<MergeDialog>('r2m-merge-dialog')!;
    await dialog.rendered();
    expect(dialog.querySelector<HTMLSelectElement>('select')?.value).toBe('bob');
    dialog.querySelector<HTMLButtonElement>('.merge__submit')!.click();
    await settle();
    await settle();
    expect(commands()).toEqual([
      { type: 'MergeCharacters', survivorId: 'bob', mergedId: 'alice', addNameAsAlias: true },
    ]);
    expect(navigation.calls.at(-1)?.url).toBe('http://localhost/app2/projects/dune/cast/bob');
  });

  it('Delete confirms, then posts DeleteCharacter and leaves the detail', async () => {
    const detail = await mount(ALICE);
    api.on('POST', COMMANDS, { outcome: 'Committed' });
    button(detail, 'delete').click();
    await settle();
    const confirm = document.querySelector('r2m-confirm-dialog')!;
    await confirm.rendered();
    expect(confirm.textContent).toContain('Delete Alice?');
    expect(confirm.textContent).not.toContain('narrates this book');
    confirm.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!.click();
    await settle();
    await settle();
    expect(commands()).toEqual([{ type: 'DeleteCharacter', characterId: 'alice' }]);
    expect(navigation.calls.at(-1)?.url).toBe('http://localhost/app2/projects/dune/cast');
  });

  it('deleting the narrating character warns that narration returns to the Narrator voice', async () => {
    const detail = await mount(row('Watson', { narratesBook: true }), [
      row('Narrator'),
      row('Watson', { narratesBook: true }),
    ]);
    button(detail, 'delete').click();
    await settle();
    const confirm = document.querySelector('r2m-confirm-dialog')!;
    await confirm.rendered();
    expect(confirm.textContent).toContain(linkedNarratorDeleteMessage('Watson'));
    confirm.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__cancel')!.click();
    await settle();
    expect(commands()).toEqual([]);
  });

  it('a line opens the reader at its chapter in Speakers mode', async () => {
    const detail = await mount(ALICE);
    detail.querySelector<HTMLButtonElement>('.character-lines__text')!.click();
    expect(navigation.calls.at(-1)?.url).toBe(
      'http://localhost/app2/projects/dune/book?mode=speakers&chapter=c7',
    );
  });

  it('a refused rename is toasted and the name stays', async () => {
    const detail = await mount(ALICE);
    api.on('POST', COMMANDS, () =>
      Response.json({ title: 'Error', status: 422, detail: 'Name taken' }, { status: 422 }),
    );
    await detail.rename('Bob');
    expect(toasts).toEqual(['problem: Name taken']);
    expect(store.selected()?.name).toBe('Alice');
  });
});
