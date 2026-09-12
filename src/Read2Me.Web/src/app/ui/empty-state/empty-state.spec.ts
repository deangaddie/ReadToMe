import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EmptyState } from './empty-state';

@Component({
  imports: [EmptyState],
  template: `
    <r2m-empty-state icon="library_books" headline="No projects yet" hint="Import a book to start.">
      <button action>New project</button>
    </r2m-empty-state>
  `,
})
class HostCmp {}

describe('r2m-empty-state', () => {
  it('renders icon, headline, hint and the action slot', async () => {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('mat-icon')?.textContent).toBe('library_books');
    expect(el.querySelector('.r2m-empty-state__headline')?.textContent).toBe('No projects yet');
    expect(el.querySelector('.r2m-empty-state__hint')?.textContent).toBe('Import a book to start.');
    expect(el.querySelector('.r2m-empty-state__action button')?.textContent).toBe('New project');
  });
});
