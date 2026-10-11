import { nothing } from 'lit-html';
import { type AiServiceDto, AiServicesApi } from '@app/api';
import { AiServicesStore } from '@app/ai-services/ai-services-store';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { adoptStyles } from '@app/core/styles';
import { computed, signal, untracked } from '@app/core/signals';
import { isAbsoluteUrl } from '@app/shared/config-form';
import { dockerControls } from '@app/ui/docker-controls';
import css from './managed-service-status.css' with { type: 'text' };

adoptStyles(css);

/** Typing a base URL asks the host which container it is once, not per keystroke. */
export const RESOLVE_DEBOUNCE_MS = 400;

/**
 * The managed container behind a config's base URL as `dockerControls()` — chip plus Start /
 * Restart / Shutdown / Refresh through the app-wide {@link AiServicesStore}, so the LLM and
 * provider editors act on the same status every other surface shows. Nothing when the watchdog
 * does not manage that URL. Settings editors feed it the *draft* URL, so a duplicate or a retyped
 * URL shows its own container (tickets 21–22, 25). An element: it owns the debounce and the
 * resolved service.
 */
export class ManagedServiceStatus extends R2mElement {
  private readonly aiServices = use(AiServicesApi);
  private readonly store = use(AiServicesStore);

  readonly #baseUrl = signal('');
  get baseUrl() {
    return this.#baseUrl();
  }
  set baseUrl(value: string) {
    this.#baseUrl.set(value);
  }

  readonly service = signal<AiServiceDto | null>(null);

  /** The URL once it is one worth asking the host about. */
  private readonly managedUrl = computed(() => {
    const url = this.#baseUrl().trim();
    return isAbsoluteUrl(url) ? url : '';
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Which URL the async answers belong to; one for an earlier URL is dropped. */
  private seq = 0;

  protected override connected(): void {
    this.effect(() => {
      const url = this.managedUrl();
      untracked(() => this.schedule(url));
    });
    this.onDisconnect(() => this.cancel());
  }

  protected template() {
    const svc = this.service();
    if (!svc) return nothing;
    const name = svc.name;
    return dockerControls({
      serviceName: svc.containerName,
      status: this.store.statusOf(name),
      busy: this.store.isBusy(name),
      onStart: () => void this.store.start(name),
      onRestart: () => void this.store.restart(name),
      onShutdown: () => void this.store.shutdown(name),
      onRefresh: () => void this.store.refresh(name),
    });
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
define('r2m-managed-service-status', ManagedServiceStatus);
