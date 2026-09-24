import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { KeyValue, KeyValueRow } from './key-value';

@Component({
  imports: [KeyValue],
  template: `<r2m-key-value [rows]="rows" dense />`,
})
class HostCmp {
  readonly rows: KeyValueRow[] = [
    { label: 'Title', value: 'Foundation' },
    { label: 'Folder', value: 'foundation', mono: true },
    { label: 'Narrator', value: null },
    { label: 'Items', value: 3415 },
  ];
}

describe('r2m-key-value', () => {
  it('renders label/value pairs, mono values and em dashes for missing values', async () => {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const labels = Array.from(el.querySelectorAll('dt')).map((n) => n.textContent?.trim());
    const values = Array.from(el.querySelectorAll('dd')).map((n) => n.textContent?.trim());
    expect(labels).toEqual(['Title', 'Folder', 'Narrator', 'Items']);
    expect(values).toEqual(['Foundation', 'foundation', '—', '3415']);
    expect(el.querySelectorAll('dd')[1]?.classList.contains('r2m-key-value__value--mono')).toBe(
      true,
    );
    expect(el.querySelectorAll('dd')[2]?.classList.contains('r2m-key-value__value--empty')).toBe(
      true,
    );
    expect(el.querySelector('r2m-key-value')?.classList.contains('r2m-key-value--dense')).toBe(
      true,
    );
  });
});
