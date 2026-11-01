import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { AppTheme, ThemeSelectionDto, ThemeSelectionUpdateRequest } from './dtos';

/** Everything the host exposes under `/api/settings/themes`. */
export class ThemesApi {
  private readonly api = use(ApiClient);

  list(): Promise<AppTheme[]> {
    return this.api.get('/api/settings/themes');
  }

  /** The host assigns `id`; a created theme is never built-in. */
  create(theme: Omit<AppTheme, 'id' | 'isBuiltIn'>): Promise<AppTheme> {
    return this.api.post('/api/settings/themes', {
      body: { ...theme, id: 0, isBuiltIn: false },
    });
  }

  update(theme: AppTheme): Promise<AppTheme> {
    return this.api.put('/api/settings/themes/{id}', { path: { id: theme.id }, body: theme });
  }

  delete(id: number): Promise<void> {
    return this.api.delete('/api/settings/themes/{id}', { path: { id } });
  }

  selection(): Promise<ThemeSelectionDto> {
    return this.api.get('/api/settings/themes/selection');
  }

  setSelection(update: ThemeSelectionUpdateRequest): Promise<ThemeSelectionDto> {
    return this.api.put('/api/settings/themes/selection', { body: update });
  }
}
