import { type ProviderSettingsSchema, toApiError } from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { LiveService, type Unsubscribe } from '@app/live/live.service';
import type { ProviderArea, ProviderAreaApi } from './provider-area';
import type { ProviderConfig } from './provider-form';

/**
 * State behind one provider settings page (native-web 35), provided by the page for its area. Every
 * write goes to the host and the store reloads from it; the hub's `settingsChanged` for the area
 * reloads it too, so an edit made in another tab or over the agent API shows up here. Reloads are
 * sequenced: a slow answer never overwrites a newer one. The page calls {@link open} on connect
 * and {@link close} on disconnect.
 */
export class ProviderSettingsStore {
  private readonly api: ProviderAreaApi;
  private readonly live = use(LiveService);
  private subscription: Unsubscribe | null = null;

  private readonly _configs = signal<ProviderConfig[] | null>(null);
  private readonly _activeId = signal<number | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly configs = this._configs.asReadonly();
  readonly activeId = this._activeId.asReadonly();
  readonly error = this._error.asReadonly();

  constructor(readonly area: ProviderArea) {
    this.api = use(area.api);
  }

  /**
   * Follows the hub's `settingsChanged` for the area and loads. The page calls it on every
   * connect, so a page the router re-attaches listens again after {@link close}.
   */
  open(): Promise<void> {
    this.subscription?.();
    this.subscription = this.live.on('settingsChanged', (m) => {
      if (m.area === this.area.key) void this.load();
    });
    return this.load();
  }

  /** Drops the hub subscription and any load still in flight. */
  close(): void {
    this.subscription?.();
    this.subscription = null;
    this.loadSeq++;
  }

  async load(): Promise<void> {
    const seq = ++this.loadSeq;
    try {
      const [configs, active] = await Promise.all([this.api.list(), this.api.active()]);
      if (seq !== this.loadSeq) return;
      this._configs.set(configs);
      this._activeId.set(active?.id ?? null);
      this._error.set(null);
    } catch (e) {
      if (seq !== this.loadSeq) return;
      this._error.set(toApiError(e).message);
    }
  }

  /** The provider type's tuning fields with ranges and defaults. */
  schema(type: number): Promise<ProviderSettingsSchema> {
    return this.api.schema(type);
  }

  async create(config: ProviderConfig): Promise<ProviderConfig> {
    const created = await this.api.create(config);
    await this.load();
    return created;
  }

  /** The stored config: the host rewrites `settingsJson` into its canonical text. */
  async update(config: ProviderConfig): Promise<ProviderConfig> {
    const stored = await this.api.update(config);
    await this.load();
    return stored;
  }

  async delete(id: number): Promise<void> {
    await this.api.delete(id);
    await this.load();
  }

  async makeActive(id: number): Promise<void> {
    await this.api.setActive(id);
    await this.load();
  }
}
