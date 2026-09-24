import { DestroyRef, Injectable, Injector, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { toApiError } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { ProviderArea, ProviderAreaApi } from './provider-area';
import { ProviderConfig } from './provider-form';

/**
 * State behind one provider settings page (ticket 22), provided by the page, which names its area
 * with `init`. Every write goes to the host and the store reloads from it; the hub's
 * `settingsChanged` for the area reloads it too, so an edit made in Blazor or over the agent API
 * shows up here. Reloads are sequenced: a slow answer never overwrites a newer one.
 */
@Injectable()
export class ProviderSettingsStore {
  private readonly injector = inject(Injector);
  private readonly live = inject(LiveService);

  private area: ProviderArea | null = null;
  private api!: ProviderAreaApi;

  private readonly _configs = signal<ProviderConfig[] | null>(null);
  private readonly _activeId = signal<number | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly configs = this._configs.asReadonly();
  readonly activeId = this._activeId.asReadonly();
  readonly error = this._error.asReadonly();

  constructor() {
    this.live
      .on('settingsChanged')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (m.area === this.area?.key) void this.load();
      });
  }

  init(area: ProviderArea): void {
    this.area = area;
    this.api = this.injector.get(area.api);
    void this.load();
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
