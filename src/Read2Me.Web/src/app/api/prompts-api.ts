import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { PromptKind } from './dtos';

/** `SettingsEndpoints.MapPromptEndpoints`: LLM prompt templates keyed by kind. */
@Injectable({ providedIn: 'root' })
export class PromptsApi {
  private readonly api = inject(ApiClient);
  private readonly base = '/api/settings/prompts';

  /** Every template, resolved (stored override or built-in default). */
  all(): Promise<Record<PromptKind, string>> {
    return this.api.get<Record<PromptKind, string>>(this.base);
  }

  set(kind: PromptKind, template: string): Promise<void> {
    return this.api.put<void>(`${this.base}/${kind}`, { template });
  }

  /** Back to the built-in default. */
  reset(kind: PromptKind): Promise<void> {
    return this.api.delete(`${this.base}/${kind}`);
  }
}
