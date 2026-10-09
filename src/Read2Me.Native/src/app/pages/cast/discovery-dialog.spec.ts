import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import type { DiscoveryOutcomeDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { FakeApi, problem } from '../../../testing/fake-api';
import { settle } from '../../../testing/fake-navigation';
import type { DiscoveryDialog } from './discovery-dialog';
import { openDiscoveryDialog } from './discovery-dialog';
import './discovery-dialog';

const DISCOVER = '/api/projects/dune/characters/discover';
const APPLY = '/api/projects/dune/characters/discover/apply';

const OUTCOME: DiscoveryOutcomeDto = {
  status: 'Ok',
  reason: null,
  characters: [
    { name: 'Alice', aliases: ['Al'], existingCharacterId: 'alice' },
    { name: 'Bob', aliases: ['Robert'], existingCharacterId: null },
  ],
  collisions: [],
};

const ROSTER = [
  { id: 'alice', name: 'Alice', aliases: ['Al'] },
  { id: 'robert', name: 'Robert Bobbington', aliases: ['Robert'] },
];

class FakeFeed {
  acquired = 0;
  released = 0;
  readonly events = signal([]);
  readonly maxUnits = 50;
  acquire() {
    this.acquired++;
  }
  release() {
    this.released++;
  }
}

let api: FakeApi;
let feed: FakeFeed;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  feed = new FakeFeed();
  override(LlmStreamFeed, feed as unknown as LlmStreamFeed);
});
afterEach(() => document.body.replaceChildren());

async function open(roster = ROSTER) {
  const result = openDiscoveryDialog({ folder: 'dune', roster });
  await settle();
  const dialog = document.querySelector<DiscoveryDialog>('r2m-discovery-dialog')!;
  await dialog.rendered();
  await settle();
  await dialog.rendered();
  return { result, dialog };
}

const phase = (dialog: Element) => dialog.querySelector('[data-phase]')?.getAttribute('data-phase');
const row = (dialog: Element, i: number) =>
  dialog.querySelector(`.discover__row[data-row="${i}"]`)!;
const button = (dialog: Element, text: string) =>
  Array.from(dialog.querySelectorAll('button')).find((b) => b.textContent?.trim() === text)!;

describe('r2m-discovery-dialog', () => {
  it('discovers on open and reviews the rows, marking the one that already exists', async () => {
    api.on('GET', DISCOVER, OUTCOME);
    api.on('POST', DISCOVER, OUTCOME);
    const { dialog } = await open();
    expect(feed.acquired).toBe(1);
    expect(phase(dialog)).toBe('review');
    expect(api.calls('POST', DISCOVER)).toHaveLength(1);
    expect(dialog.querySelectorAll('.discover__row')).toHaveLength(2);
    expect(row(dialog, 0).querySelector('.r2m-status-chip')?.textContent).toContain(
      'Already exists',
    );
    expect(row(dialog, 1).querySelector('.r2m-status-chip')).toBeNull();
    expect(dialog.textContent).toContain('2 of 2 selected');
    // Bob's alias "Robert" already belongs to Robert Bobbington.
    expect(dialog.querySelector('[data-testid=collision-warning]')?.textContent).toContain(
      'Robert',
    );
  });

  it('edits a row (rename, add and remove an alias), drops a row to clear the warning, and applies', async () => {
    api.on('POST', DISCOVER, OUTCOME);
    api.on('POST', APPLY, { applied: 2 });
    const { result, dialog } = await open();
    const bob = row(dialog, 1);
    const name = bob.querySelector<HTMLInputElement>('input[aria-label="Character name"]')!;
    name.value = 'Bobby B';
    name.dispatchEvent(new Event('input'));
    bob.querySelector<HTMLButtonElement>('.discover__add-alias')!.click();
    await dialog.rendered();
    const alias = dialog.querySelector<HTMLInputElement>('input[aria-label="New alias"]')!;
    expect(document.activeElement).toBe(alias);
    alias.value = 'Bobby';
    alias.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await dialog.rendered();
    expect(
      Array.from(row(dialog, 1).querySelectorAll('.discover__alias')).map((a) =>
        Array.from(a.childNodes)
          .filter((n) => n.nodeType === Node.TEXT_NODE)
          .map((n) => n.textContent)
          .join('')
          .trim(),
      ),
    ).toEqual(['Robert', 'Bobby']);
    row(dialog, 1).querySelector<HTMLButtonElement>('[aria-label="Remove alias Robert"]')!.click();
    await dialog.rendered();
    expect(dialog.querySelector('[data-testid=collision-warning]')).toBeNull();

    const include = row(dialog, 0).querySelector<HTMLInputElement>('input[type=checkbox]')!;
    include.checked = false;
    include.dispatchEvent(new Event('change'));
    await dialog.rendered();
    expect(dialog.textContent).toContain('1 of 2 selected');
    expect(button(dialog, 'Add 1 selected')).toBeDefined();
    dialog.querySelector<HTMLButtonElement>('[data-action=apply]')!.click();
    expect(await result).toEqual({ applied: 2 });
    expect(api.calls('POST', APPLY)[0]?.body).toEqual([{ name: 'Bobby B', aliases: ['Bobby'] }]);
    await settle();
    expect(feed.released).toBe(1);
  });

  it('a failed pass stays open with the reason; Re-run with thinking posts again', async () => {
    api.on('POST', DISCOVER, { ...OUTCOME, status: 'Failed', reason: 'bad json' });
    const { dialog } = await open();
    expect(phase(dialog)).toBe('failed');
    expect(dialog.querySelector('.discover__error')?.textContent).toContain(
      'Character discovery failed: bad json',
    );
    const rerun = dialog.querySelector<HTMLButtonElement>('[data-action=rerun]')!;
    expect(rerun.disabled).toBe(false);
    const thinking = dialog.querySelector<HTMLInputElement>('input[role=switch]')!;
    thinking.checked = true;
    thinking.dispatchEvent(new Event('change'));
    api.on('POST', DISCOVER, (_body, url) =>
      url.searchParams.get('thinking') === 'true' ? OUTCOME : problem(500, 'no thinking'),
    );
    rerun.click();
    await dialog.rendered();
    expect(phase(dialog)).toBe('discovering');
    await settle();
    await dialog.rendered();
    expect(phase(dialog)).toBe('review');
    expect(dialog.querySelector('.discover__error')).toBeNull();
  });

  it('a 422 names the missing LLM server', async () => {
    api.on('POST', DISCOVER, () => problem(422, 'No active LLM'));
    const { dialog } = await open();
    expect(phase(dialog)).toBe('failed');
    expect(dialog.querySelector('.discover__error')?.textContent).toContain(
      'No LLM server is configured',
    );
  });

  it('Cancel while discovering closes with null and drops the late answer', async () => {
    let answer!: (v: unknown) => void;
    api.on('POST', DISCOVER, () => new Promise((r) => (answer = r)));
    const { result, dialog } = await open();
    expect(phase(dialog)).toBe('discovering');
    dialog.querySelector<HTMLButtonElement>('.discover__cancel')!.click();
    expect(await result).toBeNull();
    answer(OUTCOME);
    await settle();
    expect(dialog.rows()).toEqual([]);
  });
});
