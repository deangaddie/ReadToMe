import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { PromptCatalogEntry, PromptKind } from './dtos';

/** `SettingsEndpoints.MapPromptEndpoints`: LLM prompt templates keyed by kind. */
export class PromptsApi {
  private readonly api = use(ApiClient);

  /** Every template, resolved (stored override or built-in default), keyed by every kind. */
  async all(): Promise<Record<PromptKind, string>> {
    return (await this.api.get('/api/settings/prompts')) as Record<PromptKind, string>;
  }

  /** Every kind with its copy, tokens, resolved and default templates, override flag and warnings. */
  async catalog(): Promise<PromptCatalogEntry[]> {
    return (await this.api.get('/api/settings/prompts/catalog')) as PromptCatalogEntry[];
  }

  /** Renders a template — saved or not — with the kind's sample values. */
  async preview(kind: PromptKind, template: string): Promise<string> {
    const response = await this.api.post('/api/settings/prompts/{kind}/preview', {
      path: { kind },
      body: { template },
    });
    return response.rendered;
  }

  set(kind: PromptKind, template: string): Promise<void> {
    return this.api.put('/api/settings/prompts/{kind}', { path: { kind }, body: { template } });
  }

  /** Back to the built-in default. */
  reset(kind: PromptKind): Promise<void> {
    return this.api.delete('/api/settings/prompts/{kind}', { path: { kind } });
  }
}
