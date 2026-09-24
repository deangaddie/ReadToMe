import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CountBadge } from './count-badge';

@Component({
  imports: [CountBadge],
  template: `<r2m-count-badge [count]="count()" kind="audio" title="Audio remaining" />`,
})
class HostCmp {
  readonly count = signal(3);
}

describe('r2m-count-badge', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders the count with the per-kind icon and class', async () => {
    const fixture = await mount();
    const badge = (fixture.nativeElement as HTMLElement).querySelector('r2m-count-badge')!;

    expect(badge.classList.contains('r2m-count-badge--audio')).toBe(true);
    expect(badge.querySelector('mat-icon')?.textContent).toBe('graphic_eq');
    expect(badge.querySelector('.r2m-count-badge__count')?.textContent).toBe('3');
    expect((badge as HTMLElement).hidden).toBe(false);
  });

  it('hides itself at zero', async () => {
    const fixture = await mount();
    fixture.componentInstance.count.set(0);
    await fixture.whenStable();
    const badge = (fixture.nativeElement as HTMLElement).querySelector('r2m-count-badge')!;

    expect((badge as HTMLElement).hidden).toBe(true);
  });
});
