import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { speakerHue } from '@app/shared/speaker-color';
import { SpeakerRosterEntry } from '../speaker-menu/speaker-menu';
import { SpeakerChip, SpeakerChipState } from './speaker-chip';

@Component({
  imports: [SpeakerChip],
  template: `
    <r2m-speaker-chip
      [name]="name()"
      [characterId]="id()"
      [state]="state()"
      [interactive]="interactive()"
      [roster]="roster()"
      (open)="opened = opened + 1"
      (pick)="picked.push($event)"
      (clear)="cleared = cleared + 1"
      (create)="created.push($event)"
    />
  `,
})
class HostCmp {
  readonly name = signal('Hardin');
  readonly id = signal<string | undefined>('c-hardin');
  readonly state = signal<SpeakerChipState>('named');
  readonly interactive = signal(false);
  readonly roster = signal<SpeakerRosterEntry[] | undefined>(undefined);
  opened = 0;
  picked: string[] = [];
  cleared = 0;
  created: string[] = [];
}

describe('r2m-speaker-chip', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }
  const chip = (fixture: { nativeElement: HTMLElement }) =>
    fixture.nativeElement.querySelector<HTMLElement>('r2m-speaker-chip')!;

  it('renders the name with a deterministic hue as a plain span when not interactive', async () => {
    const fixture = await mount();
    const el = chip(fixture);
    expect(el.classList.contains('r2m-speaker-chip--named')).toBe(true);
    expect(el.style.getPropertyValue('--r2m-speaker-hue')).toBe(String(speakerHue('c-hardin')));
    expect(el.querySelector('.r2m-speaker-chip__name')?.textContent).toBe('Hardin');
    expect(el.querySelector('.r2m-speaker-chip__dot')).not.toBeNull();
    expect(el.querySelector('button')).toBeNull();
  });

  it('renders the Unknown, Mixed, Narration and linked states with icons and fallback labels', async () => {
    const fixture = await mount();
    const host = fixture.componentInstance;
    host.name.set('');

    host.state.set('unknown');
    await fixture.whenStable();
    expect(chip(fixture).querySelector('mat-icon')?.textContent).toBe('question_mark');
    expect(chip(fixture).querySelector('.r2m-speaker-chip__name')?.textContent).toBe('Unknown');

    host.state.set('mixed');
    await fixture.whenStable();
    expect(chip(fixture).querySelector('mat-icon')?.textContent).toBe('call_split');
    expect(chip(fixture).querySelector('.r2m-speaker-chip__name')?.textContent).toBe('Mixed');

    host.state.set('narration');
    await fixture.whenStable();
    expect(chip(fixture).querySelector('mat-icon')?.textContent).toBe('auto_stories');

    host.name.set('Narrator');
    host.state.set('narrator-linked');
    await fixture.whenStable();
    expect(chip(fixture).querySelector('.r2m-speaker-chip__link')?.textContent).toBe('link');
    expect(chip(fixture).querySelector('.r2m-speaker-chip__dot')).not.toBeNull();
  });

  it('is a button emitting open when interactive', async () => {
    const fixture = await mount();
    fixture.componentInstance.interactive.set(true);
    await fixture.whenStable();
    const button = chip(fixture).querySelector<HTMLButtonElement>('button')!;
    button.click();
    expect(fixture.componentInstance.opened).toBe(1);
  });

  it('opens the speaker menu from a roster and relays pick', async () => {
    const fixture = await mount();
    const host = fixture.componentInstance;
    host.interactive.set(true);
    host.roster.set([
      { id: 'c-hardin', name: 'Hardin' },
      { id: 'c-pirenne', name: 'Pirenne' },
    ]);
    await fixture.whenStable();

    chip(fixture).querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable();

    const rows = document.querySelectorAll<HTMLButtonElement>('.r2m-speaker-menu__row');
    expect(rows.length).toBe(2);
    rows[1]!.click();
    await fixture.whenStable();
    expect(host.picked).toEqual(['c-pirenne']);
  });
});
