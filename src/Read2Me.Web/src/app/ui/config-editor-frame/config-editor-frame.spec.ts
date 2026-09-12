import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfigEditorFrame, dirtyGuardMessage } from './config-editor-frame';

@Component({
  imports: [ConfigEditorFrame],
  template: `<r2m-config-editor-frame
    title="Gemma 26B"
    subtitle="llama"
    [dirty]="dirty()"
    [saving]="saving()"
    [canSave]="canSave()"
    (save)="saved = true"
    (cancel)="cancelled = true"
  >
    <input id="body" />
  </r2m-config-editor-frame>`,
})
class HostCmp {
  readonly dirty = signal(false);
  readonly saving = signal(false);
  readonly canSave = signal(true);
  saved = false;
  cancelled = false;
}

describe('dirtyGuardMessage', () => {
  it('returns a message only when dirty', () => {
    expect(dirtyGuardMessage(false)).toBeNull();
    expect(dirtyGuardMessage(true)).toContain('unsaved changes');
  });
});

describe('r2m-config-editor-frame', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  function buttons(fixture: { nativeElement: HTMLElement }) {
    return Array.from(
      fixture.nativeElement.querySelectorAll('footer button'),
    ) as HTMLButtonElement[];
  }

  it('projects the body and disables both actions while clean', async () => {
    const fixture = await mount();
    expect(
      fixture.nativeElement.querySelector('.r2m-config-editor-frame__body #body'),
    ).not.toBeNull();
    expect(fixture.nativeElement.querySelector('h2')?.textContent).toBe('Gemma 26B');
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([true, true]);
    expect(fixture.nativeElement.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
  });

  it('enables Save/Cancel when dirty, gates on canSave, and shows a spinner while saving', async () => {
    const fixture = await mount();
    fixture.componentInstance.dirty.set(true);
    await fixture.whenStable();
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([false, false]);
    expect(fixture.nativeElement.querySelector('.r2m-config-editor-frame__dirty')).not.toBeNull();

    buttons(fixture)[1]!.click();
    buttons(fixture)[0]!.click();
    expect(fixture.componentInstance.saved).toBe(true);
    expect(fixture.componentInstance.cancelled).toBe(true);

    fixture.componentInstance.canSave.set(false);
    await fixture.whenStable();
    expect(buttons(fixture)[1]!.disabled).toBe(true);

    fixture.componentInstance.canSave.set(true);
    fixture.componentInstance.saving.set(true);
    await fixture.whenStable();
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([true, true]);
    expect(fixture.nativeElement.querySelector('footer mat-progress-spinner')).not.toBeNull();
    expect(buttons(fixture)[1]!.textContent).toContain('Saving…');
  });
});
