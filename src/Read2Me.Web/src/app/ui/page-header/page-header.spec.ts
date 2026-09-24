import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { PageHeader } from './page-header';

@Component({
  imports: [PageHeader],
  template: `
    <r2m-page-header title="Foundation" subtitle="Isaac Asimov">
      <span breadcrumb>Projects › Foundation</span>
      <button actions>Read book</button>
    </r2m-page-header>
  `,
})
class HostCmp {}

describe('r2m-page-header', () => {
  it('renders title, subtitle and both slots', async () => {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('h1')?.textContent).toBe('Foundation');
    expect(el.querySelector('.r2m-page-header__subtitle')?.textContent).toBe('Isaac Asimov');
    expect(el.querySelector('.r2m-page-header__breadcrumb')?.textContent).toContain('Foundation');
    expect(el.querySelector('.r2m-page-header__actions button')?.textContent).toBe('Read book');
  });
});
