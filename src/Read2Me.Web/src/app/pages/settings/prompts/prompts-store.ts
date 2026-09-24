import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PromptCatalogEntry, PromptKind, PromptsApi, toApiError } from '@app/api';
import { LiveService } from '@app/live/live.service';

/**
 * State behind the prompts page (ticket 23), provided by the page. Every write goes to the host
 * and the store reloads the catalog from it; the hub's `settingsChanged { area: 'prompts' }`
 * reloads it too, so a save made in Blazor or over the agent API shows up here. Reloads are
 * sequenced: a slow answer never overwrites a newer one.
 */
@Injectable()
export class PromptsStore {
  private readonly api = inject(PromptsApi);
  private readonly live = inject(LiveService);

  private readonly _catalog = signal<PromptCatalogEntry[] | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly catalog = this._catalog.asReadonly();
  readonly error = this._error.asReadonly();

  constructor() {
    this.live
      .on('settingsChanged')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (m.area === 'prompts') void this.load();
      });
    void this.load();
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
