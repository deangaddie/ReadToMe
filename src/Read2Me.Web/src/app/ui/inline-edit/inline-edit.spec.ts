import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InlineEdit } from './inline-edit';

@Component({
  imports: [InlineEdit],
  template: `
    <r2m-inline-edit
      value="Chapter 1"
      placeholder="Title"
      required
      (save)="saved.push($event)"
      (cancelled)="onCancel()"
    />
  `,
})
class HostCmp {
  saved: string[] = [];
  cancelled = 0;
  onCancel() {
    this.cancelled++;
  }
}

describe('r2m-inline-edit', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  function display(fixture: { nativeElement: HTMLElement }) {
    return fixture.nativeElement.querySelector<HTMLButtonElement>('.r2m-inline-edit__display');
  }

  function field(fixture: { nativeElement: HTMLElement }) {
    return fixture.nativeElement.querySelector<HTMLInputElement>('.r2m-inline-edit__input');
  }

  it('shows the value and switches to an input on click', async () => {
    const fixture = await mount();
    expect(display(fixture)?.textContent).toContain('Chapter 1');

    display(fixture)!.click();
    await fixture.whenStable();

    expect(field(fixture)?.value).toBe('Chapter 1');
  });

  it('saves a changed value on Enter', async () => {
    const fixture = await mount();
    display(fixture)!.click();
    await fixture.whenStable();

    const input = field(fixture)!;
    input.value = 'Prologue';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await fixture.whenStable();

    expect(fixture.componentInstance.saved).toEqual(['Prologue']);
    expect(field(fixture)).toBeNull();
  });

  it('cancels on Escape without emitting save', async () => {
    const fixture = await mount();
    display(fixture)!.click();
    await fixture.whenStable();

    const input = field(fixture)!;
    input.value = 'Changed';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();

    expect(fixture.componentInstance.saved).toEqual([]);
    expect(fixture.componentInstance.cancelled).toBe(1);
    expect(display(fixture)?.textContent).toContain('Chapter 1');
  });

  it('does not save an unchanged or empty required value on blur', async () => {
    const fixture = await mount();
    display(fixture)!.click();
    await fixture.whenStable();
    field(fixture)!.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(fixture.componentInstance.saved).toEqual([]);

    display(fixture)!.click();
    await fixture.whenStable();
    const input = field(fixture)!;
    input.value = '   ';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(fixture.componentInstance.saved).toEqual([]);
    expect(fixture.componentInstance.cancelled).toBe(2);
  });
});
