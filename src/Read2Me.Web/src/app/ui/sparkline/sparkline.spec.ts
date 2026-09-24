import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Sparkline } from './sparkline';

@Component({
  imports: [Sparkline],
  template: `<r2m-sparkline [values]="values()" [width]="100" [height]="20" label="Tokens/s" />`,
})
class HostCmp {
  readonly values = signal([1, 3, 2]);
}

describe('r2m-sparkline', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('scales values to the box and labels the svg', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const svg = el.querySelector('svg')!;
    const points = el.querySelector('polyline')!.getAttribute('points')!.split(' ');

    expect(svg.getAttribute('aria-label')).toBe('Tokens/s');
    expect(svg.getAttribute('viewBox')).toBe('0 0 100 20');
    expect(points).toEqual(['1.5,18.5', '50.0,1.5', '98.5,10.0']);
  });

  it('draws a centred flat line when all values are equal and nothing when empty', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;

    fixture.componentInstance.values.set([4, 4]);
    await fixture.whenStable();
    expect(el.querySelector('polyline')!.getAttribute('points')).toBe('1.5,10.0 98.5,10.0');

    fixture.componentInstance.values.set([]);
    await fixture.whenStable();
    expect(el.querySelector('polyline')).toBeNull();
  });
});
