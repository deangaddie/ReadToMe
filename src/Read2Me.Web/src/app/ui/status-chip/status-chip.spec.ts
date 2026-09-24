import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { StatusChip, StatusKind } from './status-chip';

@Component({
  imports: [StatusChip],
  template: `<r2m-status-chip [status]="status()" label="Attributing" tooltip="12 queued" />`,
})
class HostCmp {
  readonly status = signal<StatusKind>('ok');
}

describe('r2m-status-chip', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders the label with a per-status icon and class', async () => {
    const fixture = await mount();
    const chip = (fixture.nativeElement as HTMLElement).querySelector('r2m-status-chip')!;

    expect(chip.classList.contains('r2m-status-chip--ok')).toBe(true);
    expect(chip.querySelector('mat-icon')?.textContent).toBe('check_circle');
    expect(chip.querySelector('.r2m-status-chip__label')?.textContent).toBe('Attributing');
  });

  it('shows a spinner instead of an icon when busy', async () => {
    const fixture = await mount();
    fixture.componentInstance.status.set('busy');
    await fixture.whenStable();
    const chip = (fixture.nativeElement as HTMLElement).querySelector('r2m-status-chip')!;

    expect(chip.classList.contains('r2m-status-chip--busy')).toBe(true);
    expect(chip.querySelector('mat-icon')).toBeNull();
    expect(chip.querySelector('mat-progress-spinner')).not.toBeNull();
  });
});
