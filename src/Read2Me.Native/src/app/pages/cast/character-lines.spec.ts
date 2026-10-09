import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { CharacterLineDto, ParagraphContextDto } from '@app/api';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../../testing/fake-api';
import { settle } from '../../../testing/fake-navigation';
import type { CharacterLines } from './character-lines';
import { LINE_ROW_HEIGHT } from './character-lines';
import type { LineContext } from './line-context';
import './character-lines';

const lines = (count: number): CharacterLineDto[] =>
  Array.from({ length: count }, (_, i) => ({
    itemId: `i${i}`,
    paragraphId: `p${i}`,
    chapterId: 'c1',
    text: `Line ${i}`,
  }));

const CONTEXT: ParagraphContextDto = {
  before: [
    { text: 'Before.', items: [{ itemId: 'b', text: 'Before.', isDialog: false, speaker: null }] },
  ],
  paragraph: {
    text: '"Hi," said Alice.',
    items: [
      { itemId: 'i0', text: '"Hi,"', isDialog: true, speaker: 'Alice' },
      { itemId: 'i0b', text: 'said Alice.', isDialog: false, speaker: null },
    ],
  },
  after: [
    {
      text: '"Who?"',
      items: [{ itemId: 'a', text: '"Who?"', isDialog: true, speaker: null }],
    },
  ],
};

let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
});
afterEach(() => document.body.replaceChildren());

async function mount(items: CharacterLineDto[], loading = false) {
  const el = document.createElement('r2m-character-lines');
  el.folder = 'dune';
  el.lines = items;
  el.loading = loading;
  const opened: CharacterLineDto[] = [];
  el.addEventListener('open', (e) => opened.push((e as CustomEvent<CharacterLineDto>).detail));
  document.body.append(el);
  await el.rendered();
  await el.rendered();
  return { el, opened };
}

const rowsOf = (el: CharacterLines) => Array.from(el.querySelectorAll('.character-lines__row'));

describe('r2m-character-lines', () => {
  it('says so while loading and when there are no lines', async () => {
    const loading = await mount([], true);
    expect(loading.el.textContent).toContain('Loading lines…');
    document.body.replaceChildren();
    const empty = await mount([]);
    expect(empty.el.textContent).toContain('No lines attributed to this character.');
    expect(empty.el.querySelector('r2m-measured-list')).toBeNull();
  });

  it('renders one fixed-height row per line in a list up to ten rows tall', async () => {
    const { el } = await mount(lines(3));
    expect(rowsOf(el).map((r) => r.getAttribute('data-item-id'))).toEqual(['i0', 'i1', 'i2']);
    expect(el.list?.style.height).toBe(`${3 * LINE_ROW_HEIGHT + 2}px`);
    document.body.replaceChildren();
    const many = await mount(lines(40));
    expect(many.el.list?.style.height).toBe(`${10 * LINE_ROW_HEIGHT + 2}px`);
  });

  it('clicking the text emits open with the line', async () => {
    const { el, opened } = await mount(lines(2));
    el.querySelector<HTMLButtonElement>('[data-item-id="i1"] .character-lines__text')!.click();
    expect(opened.map((l) => l.itemId)).toEqual(['i1']);
  });

  it('the chevron expands one line at a time and its context loads on demand', async () => {
    const { el } = await mount(lines(2));
    const toggle = (id: string) =>
      el.querySelector<HTMLButtonElement>(`[data-item-id="${id}"] .character-lines__toggle`)!;
    expect(toggle('i0').getAttribute('aria-expanded')).toBe('false');

    toggle('i0').click();
    await el.rendered();
    await el.rendered();
    expect(toggle('i0').getAttribute('aria-expanded')).toBe('true');
    expect(toggle('i0').getAttribute('aria-label')).toBe('Hide context');
    expect(el.querySelector('.character-lines__row--expanded')?.getAttribute('data-item-id')).toBe(
      'i0',
    );
    expect(el.querySelector('.character-lines__context-line')?.textContent?.trim()).toBe('Line 0');
    const context = el.querySelector<LineContext>('r2m-line-context')!;
    await context.rendered();
    expect(context.querySelector('[data-action="load-context"]')).not.toBeNull();

    api.on('GET', '/api/projects/dune/paragraphs/p0/context', (_, url) => {
      expect(url.searchParams.get('chapterId')).toBe('c1');
      expect(url.searchParams.get('before')).toBe('3');
      expect(url.searchParams.get('after')).toBe('2');
      return CONTEXT;
    });
    context.querySelector<HTMLButtonElement>('[data-action="load-context"]')!.click();
    await settle();
    await context.rendered();
    const paragraphs = Array.from(context.querySelectorAll('.line-context__paragraph'));
    expect(paragraphs).toHaveLength(3);
    expect(paragraphs[1]?.classList).toContain('line-context__paragraph--query');
    expect(paragraphs[0]?.querySelector('.line-context__narration')).not.toBeNull();
    const chips = Array.from(context.querySelectorAll('r2m-speaker-chip'));
    expect(chips.map((c) => c.name)).toEqual(['Alice', '?']);
    expect(chips.map((c) => c.state)).toEqual(['named', 'unknown']);

    // Switching to another line swaps the context and resets its window.
    toggle('i1').click();
    await el.rendered();
    await el.rendered();
    expect(el.querySelector('.character-lines__row--expanded')?.getAttribute('data-item-id')).toBe(
      'i1',
    );
    const next = el.querySelector<LineContext>('r2m-line-context')!;
    await next.rendered();
    expect(next.context()).toBeNull();
    expect(next.querySelector('[data-action="load-context"]')).not.toBeNull();

    toggle('i1').click();
    await el.rendered();
    await el.rendered();
    expect(el.querySelector('r2m-line-context')).toBeNull();
  });

  it('+ previous and + next widen the window up to the cap, and a failure shows its message', async () => {
    const { el } = await mount(lines(1));
    el.querySelector<HTMLButtonElement>('.character-lines__toggle')!.click();
    await el.rendered();
    await el.rendered();
    const context = el.querySelector<LineContext>('r2m-line-context')!;
    const windows: [string | null, string | null][] = [];
    api.on('GET', '/api/projects/dune/paragraphs/p0/context', (_, url) => {
      windows.push([url.searchParams.get('before'), url.searchParams.get('after')]);
      return CONTEXT;
    });
    await context.load();
    await context.more('before');
    await context.more('before');
    await context.more('before');
    await context.more('after');
    expect(windows).toEqual([
      ['3', '2'],
      ['6', '2'],
      ['9', '2'],
      ['10', '2'],
      ['10', '4'],
    ]);
    await context.rendered();
    expect(context.querySelector('[data-action="context-previous"]')).toBeNull();
    expect(context.querySelector('[data-action="context-next"]')).not.toBeNull();

    api.on('GET', '/api/projects/dune/paragraphs/p0/context', () => problem(404, 'not in chapter'));
    await context.more('after');
    await context.rendered();
    expect(context.querySelector('.line-context__error')?.textContent).toContain('not in chapter');
  });
});
