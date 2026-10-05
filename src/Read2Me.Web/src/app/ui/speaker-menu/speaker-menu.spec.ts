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
    // Nothing is highlighted on open: the first ArrowDown lands on the narrator, the second on Anselm.
    for (let i = 0; i < 2; i++) {
      const down = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true });
      Object.defineProperty(down, 'keyCode', { value: 40 });
      menu.dispatchEvent(down);
    }
    menu.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(fixture.componentInstance.picked).toEqual(['c-anselm']);
  });

  it('typing highlights the first match, and Enter picks it', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    input.value = 'ha';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(el.querySelector('.r2m-speaker-menu__row--active')?.textContent).toContain('Hardin');

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(fixture.componentInstance.picked).toEqual(['c-hardin']);
  });

  it('highlights while typing the way a browser sends keys (keydown before input)', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    // Every name has an 'n': the row set does not change, so nothing forces a rebuild.
    for (const key of 'n') {
      input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
      input.value += key;
      input.dispatchEvent(new Event('input'));
      await fixture.whenStable();
    }
    expect(el.querySelector('.r2m-speaker-menu__row--active')?.textContent).toContain('Narrator');
  });

  it('Enter straight after typing picks the first match, before the list has re-rendered', () => {
    return mount().then((fixture) => {
      const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
        '.r2m-speaker-menu__input',
      )!;
      input.value = 'pir';
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      expect(fixture.componentInstance.picked).toEqual(['c-pirenne']);
    });
  });

  it('deleting the search back to empty drops the highlight, so Enter picks nobody', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    input.value = 'ha';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    input.value = '';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(el.querySelector('.r2m-speaker-menu__row--active')).toBeNull();

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(fixture.componentInstance.picked).toEqual([]);
  });

  it('Enter on a search that matches nobody creates that character', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    input.value = ' Gaal ';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(fixture.componentInstance.created).toEqual(['Gaal']);
    expect(fixture.componentInstance.picked).toEqual([]);
  });

  it('a bare Enter in the empty search picks nobody', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('.r2m-speaker-menu__input')!;

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();
    expect(fixture.componentInstance.picked).toEqual([]);
    expect(fixture.componentInstance.created).toEqual([]);
  });
});
