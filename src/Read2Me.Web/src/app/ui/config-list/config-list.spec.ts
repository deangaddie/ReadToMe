import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfigList, ConfigListItem } from './config-list';

@Component({
  imports: [ConfigList],
  template: `<r2m-config-list
    [items]="items"
    [selectedId]="'b'"
    (select)="selected = $event"
    (create)="created = true"
    (makeActive)="activated = $event"
    (duplicate)="duplicated = $event"
    (delete)="deleted = $event"
  />`,
})
class HostCmp {
  items: ConfigListItem[] = [
    { id: 'a', name: 'Gemma 26B', subtitle: 'llama · registry', isActive: true },
    { id: 'b', name: 'Qwen 9B', subtitle: 'llama', badge: 'GPU' },
  ];
  selected = '';
  created = false;
  activated = '';
  duplicated = '';
  deleted = '';
}

describe('r2m-config-list', () => {
  async function mount(items?: ConfigListItem[]) {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    if (items) fixture.componentInstance.items = items;
    await fixture.whenStable();
    return fixture;
  }

  function rows(fixture: { nativeElement: HTMLElement }) {
    return Array.from(fixture.nativeElement.querySelectorAll('.r2m-config-list__row'));
  }

  it('renders rows with active marker, badge and selection', async () => {
    const fixture = await mount();
    const r = rows(fixture);
    expect(r.length).toBe(2);
    expect(r[0]?.querySelector('r2m-status-chip')?.textContent).toContain('Active');
    expect(r[1]?.querySelector('.r2m-config-list__badge')?.textContent).toBe('GPU');
    expect(r[1]?.classList.contains('r2m-config-list__row--selected')).toBe(true);
  });

  it('filters by name or subtitle and emits select / create', async () => {
    const fixture = await mount();
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.value = 'registry';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(rows(fixture).length).toBe(1);
    expect(rows(fixture)[0]?.querySelector('.r2m-config-list__name')?.textContent).toBe(
      'Gemma 26B',
    );

    (rows(fixture)[0]?.querySelector('.r2m-config-list__main') as HTMLButtonElement).click();
    (
      fixture.nativeElement.querySelector('.r2m-config-list__head button') as HTMLButtonElement
    ).click();
    expect(fixture.componentInstance.selected).toBe('a');
    expect(fixture.componentInstance.created).toBe(true);
  });

  it('shows the empty text when there are no items', async () => {
    const fixture = await mount([]);
    expect(fixture.nativeElement.querySelector('.r2m-config-list__empty')?.textContent).toContain(
      'No configurations yet.',
    );
  });

  it('exposes Make active / Duplicate / Delete in the row menu', async () => {
    const fixture = await mount();
    (rows(fixture)[1]?.querySelector('[mat-icon-button]') as HTMLButtonElement).click();
    await fixture.whenStable();
    const menuItems = Array.from(document.querySelectorAll('.mat-mdc-menu-panel button'));
    expect(menuItems.map((b) => b.querySelector('span')?.textContent?.trim())).toEqual([
      'Make active',
      'Duplicate',
      'Delete',
    ]);
    (menuItems[2] as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.componentInstance.deleted).toBe('b');
  });
});
