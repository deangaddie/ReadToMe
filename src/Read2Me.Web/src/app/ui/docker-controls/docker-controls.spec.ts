import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AiServiceStatus, DockerControls } from './docker-controls';

@Component({
  imports: [DockerControls],
  template: `<r2m-docker-controls
    serviceName="llama"
    [status]="status()"
    [busy]="busy()"
    (start)="log.push('start')"
    (restart)="log.push('restart')"
    (shutdown)="log.push('shutdown')"
    (refresh)="log.push('refresh')"
  />`,
})
class HostCmp {
  readonly status = signal<AiServiceStatus>('Stopped');
  readonly busy = signal(false);
  log: string[] = [];
}

describe('r2m-docker-controls', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  function buttons(fixture: { nativeElement: HTMLElement }) {
    return Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[];
  }

  it('enables Start only when stopped and emits it', async () => {
    const fixture = await mount();
    expect(fixture.nativeElement.querySelector('r2m-status-chip')?.textContent).toContain(
      'Stopped',
    );
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([false, true, true, false]);
    buttons(fixture)[0]!.click();
    buttons(fixture)[3]!.click();
    expect(fixture.componentInstance.log).toEqual(['start', 'refresh']);
  });

  it('enables Restart/Shutdown when ready or starting', async () => {
    const fixture = await mount();
    fixture.componentInstance.status.set('Ready');
    await fixture.whenStable();
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([true, false, false, false]);
    expect(fixture.nativeElement.querySelector('r2m-status-chip')?.classList).toContain(
      'r2m-status-chip--ok',
    );

    fixture.componentInstance.status.set('Starting');
    await fixture.whenStable();
    expect(buttons(fixture).map((b) => b.disabled)).toEqual([true, false, false, false]);
    expect(fixture.nativeElement.querySelector('r2m-status-chip')?.classList).toContain(
      'r2m-status-chip--busy',
    );
  });

  it('disables everything while busy and flags NotFound as a warning', async () => {
    const fixture = await mount();
    fixture.componentInstance.status.set('NotFound');
    fixture.componentInstance.busy.set(true);
    await fixture.whenStable();
    expect(buttons(fixture).every((b) => b.disabled)).toBe(true);
    expect(fixture.nativeElement.querySelector('r2m-status-chip')?.classList).toContain(
      'r2m-status-chip--warn',
    );
  });
});
