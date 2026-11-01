import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { PromptCatalogEntry, PromptKind, PromptPreviewResponse } from './dtos';

/** `SettingsEndpoints.MapPromptEndpoints`: LLM prompt templates keyed by kind. */
export class PromptsApi {
  private readonly api = use(ApiClient);
  private readonly base = '/api/settings/prompts';

  /** Every template, resolved (stored override or built-in default). */
  all(): Promise<Record<PromptKind, string>> {
    return this.api.untypedGet<Record<PromptKind, string>>(this.base);
  }

  /** Every kind with its copy, tokens, resolved and default templates, override flag and warnings. */
  catalog(): Promise<PromptCatalogEntry[]> {
    return this.api.untypedGet<PromptCatalogEntry[]>(`${this.base}/catalog`);
  }

  /** Renders a template — saved or not — with the kind's sample values. */
  async preview(kind: PromptKind, template: string): Promise<string> {
    const response = await this.api.untypedPost<PromptPreviewResponse>(
      `${this.base}/${kind}/preview`,
      {
        template,
      },
    );
    return response.rendered;
  }

  set(kind: PromptKind, template: string): Promise<void> {
    return this.api.untypedPut<void>(`${this.base}/${kind}`, { template });
  }

  /** Back to the built-in default. */
  reset(kind: PromptKind): Promise<void> {
    return this.api.untypedDelete(`${this.base}/${kind}`);
  }
}
