import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AiServiceDto, AiServicesApi } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';
import { ManagedServiceStatus } from './managed-service-status';

const LLAMA: AiServiceDto = {
  name: 'llama',
  containerName: 'read2me-llama',
  baseUrl: 'http://localhost:8080',
  usesGpu: true,
};

@Component({
  imports: [ManagedServiceStatus],
  template: `<app-managed-service-status [baseUrl]="baseUrl()" />`,
})
class HostCmp {
  readonly baseUrl = signal('http://localhost:8080');
}

describe('app-managed-service-status', () => {
  const live = {
    serviceStatus: signal<Record<string, 'Ready' | 'Stopped' | 'Unknown'>>({}),
    on: () => new Subject<never>().asObservable(),
  };
  let api: Record<
    'resolve' | 'status' | 'restart' | 'start' | 'shutdown',
    ReturnType<typeof vi.fn>
  >;

  async function mount() {
    await TestBed.configureTestingModule({
      imports: [HostCmp],
      providers: [
        { provide: LiveService, useValue: live },
        { provide: AiServicesApi, useValue: api },
        {
          provide: ToastService,
          useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn(), problem: vi.fn() },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    fixture.detectChanges();
    // Past the 400 ms resolve debounce.
    await new Promise((resolve) => setTimeout(resolve, 450));
    await fixture.whenStable();
    return fixture;
  }

  beforeEach(() => {
    live.serviceStatus.set({});
    api = {
      resolve: vi.fn().mockResolvedValue(LLAMA),
      status: vi.fn().mockResolvedValue({ name: 'llama', status: 'Ready' }),
      restart: vi.fn().mockResolvedValue(undefined),
      start: vi.fn().mockResolvedValue(undefined),
      shutdown: vi.fn().mockResolvedValue(undefined),
    };
  });

  it('resolves the draft URL, probes an unobserved service once and shows the hub-fed chip with controls', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(api.resolve).toHaveBeenCalledWith('http://localhost:8080');
    expect(api.status).toHaveBeenCalledWith('llama');

    live.serviceStatus.set({ llama: 'Ready' });
    await fixture.whenStable();
    expect(el.querySelector('r2m-status-chip')?.textContent).toContain('Ready');
    expect(el.textContent).toContain('read2me-llama');

    (el.querySelector('button[aria-label="Restart"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(api.restart).toHaveBeenCalledWith('llama');
  });

  it('renders nothing for a URL the watchdog does not manage', async () => {
    api.resolve.mockResolvedValue(null);
    const fixture = await mount();
    expect((fixture.nativeElement as HTMLElement).querySelector('r2m-docker-controls')).toBeNull();
    expect(api.status).not.toHaveBeenCalled();
  });
});
