import { afterEach, describe, expect, it } from 'bun:test';
import './config-list';
import type { ConfigList, ConfigListItem } from './config-list';

const ITEMS: ConfigListItem[] = [
  { id: 'a', name: 'Gemma 26B', subtitle: 'llama · registry', isActive: true },
  { id: 'b', name: 'Qwen 9B', subtitle: 'llama', badge: 'GPU' },
];

async function mount(items: ConfigListItem[] = ITEMS) {
  const list = document.createElement('r2m-config-list') as ConfigList;
  list.items = items;
  list.selectedId = 'b';
  const events: string[] = [];
  for (const type of ['pick', 'create', 'make-active', 'duplicate', 'delete'])
    list.addEventListener(type, (e) => events.push(`${type}:${(e as CustomEvent).detail ?? ''}`));
  document.body.append(list);
  await list.rendered();
  return { list, events };
}

const rows = (list: Element) => Array.from(list.querySelectorAll('.r2m-config-list__row'));

afterEach(() => document.body.replaceChildren());

describe('r2m-config-list', () => {
  it('renders rows with active marker, badge and selection', async () => {
    const { list } = await mount();
    const r = rows(list);
    expect(r.length).toBe(2);
    expect(r[0]?.querySelector('.r2m-status-chip')?.textContent).toContain('Active');
    expect(r[1]?.querySelector('.r2m-config-list__badge')?.textContent).toBe('GPU');
    expect(r[1]?.classList.contains('r2m-config-list__row--selected')).toBe(true);
    expect(r[1]?.getAttribute('aria-selected')).toBe('true');
  });

  it('names the active marker after the area', async () => {
    const { list } = await mount();
    list.activeLabel = 'Default';
    await list.rendered();
    expect(rows(list)[0]?.querySelector('.r2m-status-chip')?.textContent).toContain('Default');
    expect(list.querySelector('[role="menuitem"]')?.textContent).toContain('Make default');
  });

  it('filters by name or subtitle and emits pick / create', async () => {
    const { list, events } = await mount();
    const input = list.querySelector<HTMLInputElement>('input[type="search"]')!;
    input.value = 'registry';
    input.dispatchEvent(new Event('input'));
    await list.rendered();
    expect(rows(list).length).toBe(1);
    expect(rows(list)[0]?.querySelector('.r2m-config-list__name')?.textContent).toBe('Gemma 26B');

    rows(list)[0]?.querySelector<HTMLButtonElement>('.r2m-config-list__main')!.click();
    list.querySelector<HTMLButtonElement>('.r2m-config-list__head button')!.click();
    expect(events).toEqual(['pick:a', 'create:']);
  });

  it('shows the empty text when there are no items, and the no-match text for a search', async () => {
    const { list } = await mount([]);
    expect(list.querySelector('.r2m-config-list__empty')?.textContent).toContain(
      'No configurations yet.',
    );
    list.items = ITEMS;
    const input = list.querySelector<HTMLInputElement>('input[type="search"]')!;
    input.value = 'zzz';
    input.dispatchEvent(new Event('input'));
    await list.rendered();
    expect(list.querySelector('.r2m-config-list__empty')?.textContent).toContain(
      'Nothing matches your search.',
    );
  });

  it('offers Make active / Duplicate / Delete per row, Make active disabled on the active one', async () => {
    const { list, events } = await mount();
    const [active, other] = rows(list);
    const entries = (row: Element | undefined) =>
      Array.from(row?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);
    expect(
      entries(other).map((b) => b.querySelector('span:last-of-type')?.textContent?.trim()),
    ).toEqual(['Make active', 'Duplicate', 'Delete']);
    expect(entries(active)[0]?.disabled).toBe(true);
    expect(entries(other)[0]?.disabled).toBe(false);
    expect(other?.querySelector('.r2m-config-list__more')?.getAttribute('aria-label')).toBe(
      'Actions for Qwen 9B',
    );

    entries(other)[0]!.click();
    entries(other)[1]!.click();
    entries(other)[2]!.click();
    expect(events).toEqual(['make-active:b', 'duplicate:b', 'delete:b']);
  });

  it('disables New and the row menus while busy', async () => {
    const { list } = await mount();
    list.busy = true;
    await list.rendered();
    expect(list.querySelector<HTMLButtonElement>('.r2m-config-list__head button')?.disabled).toBe(
      true,
    );
    expect(list.querySelector<HTMLButtonElement>('.r2m-config-list__more')?.disabled).toBe(true);
  });
});
