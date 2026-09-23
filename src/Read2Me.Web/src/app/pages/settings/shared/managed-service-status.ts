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
import { AiServiceDto, AiServicesApi } from '@app/api';
import { AiServicesStore } from '@app/ai-services/ai-services-store';
import { isAbsoluteUrl } from '@app/shared/config-form';
import { DockerControls } from '@app/ui/docker-controls/docker-controls';

/** Typing a base URL asks the host which container it is once, not per keystroke. */
const RESOLVE_DEBOUNCE_MS = 400;

/**
 * The managed container behind a config's base URL as `r2m-docker-controls` — chip plus Start /
 * Restart / Shutdown / Refresh through the app-wide {@link AiServicesStore}, so the LLM and provider
 * editors act on the same status every other surface shows. Nothing when the watchdog does not
 * manage that URL. Settings editors feed it the *draft* URL, so a duplicate or a retyped URL shows
 * its own container (tickets 21–22, 25).
 */
@Component({
  selector: 'app-managed-service-status',
  imports: [DockerControls],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (service(); as svc) {
      <r2m-docker-controls
        [serviceName]="svc.containerName"
        [status]="store.statusOf(svc.name)"
        [busy]="store.isBusy(svc.name)"
        (start)="store.start(svc.name)"
        (restart)="store.restart(svc.name)"
        (shutdown)="store.shutdown(svc.name)"
        (refresh)="store.refresh(svc.name)"
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
  protected readonly store = inject(AiServicesStore);

  readonly baseUrl = input.required<string>();

  protected readonly service = signal<AiServiceDto | null>(null);

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
    if (!url) return;
    this.timer = setTimeout(() => void this.resolve(url), RESOLVE_DEBOUNCE_MS);
  }

  private async resolve(baseUrl: string): Promise<void> {
    const seq = this.seq;
    try {
      const service = await this.aiServices.resolve(baseUrl);
      if (seq !== this.seq) return;
      this.service.set(service);
      // A status nobody has observed yet is probed once; a known one is left to the hub.
      if (service && this.store.statusOf(service.name) === 'Unknown') {
        await this.store.refresh(service.name);
      }
    } catch {
      // Not knowing whether the URL is a managed container only hides the status row.
    }
  }
}
