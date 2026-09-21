import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { AiServiceDto, AiServiceStatus, AiServicesApi } from '@app/api';
import { isAbsoluteUrl } from '@app/shared/config-form';
import { DockerControls } from '@app/ui/docker-controls/docker-controls';

/** Typing a base URL asks the host which container it is once, not per keystroke. */
const RESOLVE_DEBOUNCE_MS = 400;

/**
 * The managed container behind a config's base URL, status-only (`r2m-docker-controls[statusOnly]`:
 * chip + Refresh) — nothing when the watchdog does not manage that URL. Settings editors feed it the
 * *draft* URL, so a duplicate or a retyped URL shows its own container (tickets 21–22).
 */
@Component({
  selector: 'app-managed-service-status',
  imports: [DockerControls],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (service(); as svc) {
      <r2m-docker-controls
        statusOnly
        [serviceName]="svc.containerName"
        [status]="status()"
        [busy]="probing()"
        (refresh)="probe()"
      />
    }
  `,
  styles: `
    /* No box of its own: an unmanaged URL leaves no gap in the editor's column. */
    :host {
      display: contents;
    }
  `,
})
export class ManagedServiceStatus {
  private readonly aiServices = inject(AiServicesApi);

  readonly baseUrl = input.required<string>();

  protected readonly service = signal<AiServiceDto | null>(null);
  protected readonly status = signal<AiServiceStatus>('Unknown');
  protected readonly probing = signal(false);

  /** The URL once it is one worth asking the host about. */
  private readonly managedUrl = computed(() => {
    const url = this.baseUrl().trim();
    return isAbsoluteUrl(url) ? url : '';
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Which URL the async answers belong to; one for an earlier URL is dropped. */
  private seq = 0;

  constructor() {
    effect(() => {
      const url = this.managedUrl();
      untracked(() => this.schedule(url));
    });
    inject(DestroyRef).onDestroy(() => this.cancel());
  }

  private cancel(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.seq++;
  }

  private schedule(url: string): void {
    this.cancel();
    this.service.set(null);
    this.status.set('Unknown');
    this.probing.set(false);
    if (!url) return;
    this.timer = setTimeout(() => void this.resolve(url), RESOLVE_DEBOUNCE_MS);
  }

  private async resolve(baseUrl: string): Promise<void> {
    const seq = this.seq;
    try {
      const service = await this.aiServices.resolve(baseUrl);
      if (seq !== this.seq) return;
      this.service.set(service);
      if (service) await this.probe();
    } catch {
      // Not knowing whether the URL is a managed container only hides the status row.
    }
  }

  protected async probe(): Promise<void> {
    const service = this.service();
    if (!service) return;
    const seq = this.seq;
    this.probing.set(true);
    try {
      const { status } = await this.aiServices.status(service.name);
      if (seq === this.seq) this.status.set(status);
    } catch {
      if (seq === this.seq) this.status.set('Unknown');
    } finally {
      if (seq === this.seq) this.probing.set(false);
    }
  }
}
