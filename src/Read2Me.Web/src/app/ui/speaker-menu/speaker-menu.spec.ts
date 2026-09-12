import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SpeakerMenu, SpeakerRosterEntry } from './speaker-menu';

const ROSTER: SpeakerRosterEntry[] = [
  { id: 'c-hardin', name: 'Hardin', aliases: ['Salvor'] },
  { id: 'c-pirenne', name: 'Pirenne' },
  { id: 'c-narr', name: 'Narrator', isNarrator: true },
  { id: 'c-anselm', name: 'Anselm' },
];

@Component({
  imports: [SpeakerMenu],
  template: `
    <r2m-speaker-menu
      [roster]="roster()"
      selectedId="c-pirenne"
      (pick)="picked.push($event)"
      (clear)="cleared = cleared + 1"
      (create)="created.push($event)"
    />
  `,
})
class HostCmp {
  readonly roster = signal(ROSTER);
  picked: string[] = [];
  cleared = 0;
  created: string[] = [];
}

describe('r2m-speaker-menu', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  const names = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('.r2m-speaker-menu__name')).map((n) => n.textContent);

  it('lists the narrator first, then the rest alphabetically, marking the selection', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(names(el)).toEqual(['Narrator', 'Anselm', 'Hardin', 'Pirenne']);
    expect(el.querySelector('.r2m-speaker-menu__row--selected')?.textContent).toContain('Pirenne');
  });

  it('filters by name or alias, case-insensitively', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    input.value = 'salv';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(names(el)).toEqual(['Hardin']);

    input.value = 'zzz';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(el.querySelector('.r2m-speaker-menu__empty')?.textContent).toBe('No matches');
  });

  it('emits pick on row click, clear and create with the search text from the footer', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const host = fixture.componentInstance;

    el.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row')[2]!.click();
    expect(host.picked).toEqual(['c-hardin']);

    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;
    input.value = 'Gaal';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    const actions = el.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__action');
    actions[0]!.click();
    actions[1]!.click();
    expect(host.cleared).toBe(1);
    expect(host.created).toEqual(['Gaal']);
  });

  it('navigates with arrow keys and picks the active row on Enter', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const menu = el.querySelector('r2m-speaker-menu')!;

    // CDK key managers read the legacy keyCode, which jsdom does not derive from `key`.
    const down = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true });
    Object.defineProperty(down, 'keyCode', { value: 40 });
    menu.dispatchEvent(down);
    menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(fixture.componentInstance.picked).toEqual(['c-anselm']);
  });
});
