import { type PromptCatalogEntry, type PromptKind, PromptsApi, toApiError } from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { LiveService, type Unsubscribe } from '@app/live/live.service';

/**
 * State behind the prompts page (ticket 23), provided by the page. Every write goes to the host
 * and the store reloads the catalog from it; the hub's `settingsChanged { area: 'prompts' }`
 * reloads it too, so a save made in another tab or over the agent API shows up here. Reloads are
 * sequenced: a slow answer never overwrites a newer one. The page calls {@link open} on connect
 * and {@link close} on disconnect.
 */
export class PromptsStore {
  private readonly api = use(PromptsApi);
  private readonly live = use(LiveService);
  private subscription: Unsubscribe | null = null;

  private readonly _catalog = signal<PromptCatalogEntry[] | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly catalog = this._catalog.asReadonly();
  readonly error = this._error.asReadonly();

  /**
   * Follows the hub's `settingsChanged { area: 'prompts' }` and loads. The page calls it on every
   * connect, so a page the router re-attaches listens again after {@link close}.
   */
  open(): Promise<void> {
    this.subscription?.();
    this.subscription = this.live.on('settingsChanged', (m) => {
      if (m.area === 'prompts') void this.load();
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
      const catalog = await this.api.catalog();
      if (seq !== this.loadSeq) return;
      this._catalog.set(catalog);
      this._error.set(null);
    } catch (e) {
      if (seq !== this.loadSeq) return;
      this._error.set(toApiError(e).message);
    }
  }

  async save(kind: PromptKind, template: string): Promise<void> {
    await this.api.set(kind, template);
    await this.load();
  }

  async reset(kind: PromptKind): Promise<void> {
    await this.api.reset(kind);
    await this.load();
  }

  /** Renders any template — the unsaved draft included — with the kind's sample values. */
  preview(kind: PromptKind, template: string): Promise<string> {
    return this.api.preview(kind, template);
  }
}
