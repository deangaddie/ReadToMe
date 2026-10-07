import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { AppTheme, ThemeSelection, ThemeSelectionUpdate } from './dtos';

/** Everything the host exposes under `/api/settings/themes`. */
export class ThemesApi {
  private readonly api = use(ApiClient);
  private readonly base = '/api/settings/themes';

  list(): Promise<AppTheme[]> {
    return this.api.get<AppTheme[]>(this.base);
  }

  create(theme: Omit<AppTheme, 'id' | 'isBuiltIn'>): Promise<AppTheme> {
    return this.api.post<AppTheme>(this.base, theme);
  }

  update(theme: AppTheme): Promise<AppTheme> {
    return this.api.put<AppTheme>(`${this.base}/${theme.id}`, theme);
  }

  delete(id: number): Promise<void> {
    return this.api.delete(`${this.base}/${id}`);
  }

  selection(): Promise<ThemeSelection> {
    return this.api.get<ThemeSelection>(`${this.base}/selection`);
  }

  setSelection(update: ThemeSelectionUpdate): Promise<ThemeSelection> {
    return this.api.put<ThemeSelection>(`${this.base}/selection`, update);
  }
}
