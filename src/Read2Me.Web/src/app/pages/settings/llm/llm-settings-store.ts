import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AttributionChainResponse,
  AttributionChainStep,
  LlmServerConfig,
  LlmSettingsApi,
  toApiError,
} from '@app/api';
import { LiveService } from '@app/live/live.service';
import { chainOptions, chainRows } from './chain-steps';

/**
 * State behind `/settings/llm` (ticket 21), provided by the page. Every write goes to the host and
 * the store reloads from it; the hub's `settingsChanged { area: 'llm' }` reloads it too, so an edit
 * made in Blazor or over the agent API shows up here. Reloads are sequenced: a slow answer never
 * overwrites a newer one.
 */
@Injectable()
export class LlmSettingsStore {
  private readonly api = inject(LlmSettingsApi);
  private readonly live = inject(LiveService);

  private readonly _configs = signal<LlmServerConfig[] | null>(null);
  private readonly _activeId = signal<number | null>(null);
  private readonly _chain = signal<AttributionChainResponse | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly configs = this._configs.asReadonly();
  readonly activeId = this._activeId.asReadonly();
  readonly error = this._error.asReadonly();

  readonly active = computed(() => this._configs()?.find((c) => c.id === this._activeId()) ?? null);

  readonly chainLoaded = computed(() => this._chain() !== null);
  readonly steps = computed(() => this._chain()?.steps ?? []);
  readonly selfConsistency = computed(() => this._chain()?.selfConsistency ?? false);
  readonly chainRows = computed(() => chainRows(this.steps(), this._chain()?.available ?? []));
  readonly chainOptions = computed(() =>
    chainOptions(this.steps(), this._chain()?.available ?? []),
  );
  /** What attribution runs when the chain is empty: the default config, or nothing. */
  readonly chainFallback = computed(() => this._chain()?.resolved[0]?.config ?? null);

  constructor() {
    this.live
      .on('settingsChanged')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (m.area === 'llm') void this.load();
      });
  }

  async load(): Promise<void> {
    const seq = ++this.loadSeq;
    try {
      const [configs, active, chain] = await Promise.all([
        this.api.list(),
        this.api.active(),
        this.api.attributionChain(),
      ]);
      if (seq !== this.loadSeq) return;
      this._configs.set(configs);
      this._activeId.set(active?.id ?? null);
      this._chain.set(chain);
      this._error.set(null);
    } catch (e) {
      if (seq !== this.loadSeq) return;
      this._error.set(toApiError(e).message);
    }
  }

  async create(config: LlmServerConfig): Promise<LlmServerConfig> {
    const created = await this.api.create(config);
    await this.load();
    return created;
  }

  async update(config: LlmServerConfig): Promise<void> {
    await this.api.update(config);
    await this.load();
  }

  async delete(id: number): Promise<void> {
    await this.api.delete(id);
    await this.load();
  }

  async makeDefault(id: number): Promise<void> {
    await this.api.setActive(id);
    await this.load();
  }

  async saveChain(steps: AttributionChainStep[], selfConsistency: boolean): Promise<void> {
    // Holds off any reload already in flight: it read the chain before this write.
    this.loadSeq++;
    this._chain.set(await this.api.setAttributionChain({ steps, selfConsistency }));
  }
}
