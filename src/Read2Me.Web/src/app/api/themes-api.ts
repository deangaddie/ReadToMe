import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { AppTheme, ThemeSelection, ThemeSelectionUpdate } from './dtos';

/** Everything the host exposes under `/api/settings/themes`. */
export class ThemesApi {
  private readonly api = use(ApiClient);
  private readonly base = '/api/settings/themes';

  list(): Promise<AppTheme[]> {
    return this.api.untypedGet<AppTheme[]>(this.base);
  }

  create(theme: Omit<AppTheme, 'id' | 'isBuiltIn'>): Promise<AppTheme> {
    return this.api.untypedPost<AppTheme>(this.base, theme);
  }

  update(theme: AppTheme): Promise<AppTheme> {
    return this.api.untypedPut<AppTheme>(`${this.base}/${theme.id}`, theme);
  }

  delete(id: number): Promise<void> {
    return this.api.untypedDelete(`${this.base}/${id}`);
  }

  selection(): Promise<ThemeSelection> {
    return this.api.untypedGet<ThemeSelection>(`${this.base}/selection`);
  }

  setSelection(update: ThemeSelectionUpdate): Promise<ThemeSelection> {
    return this.api.untypedPut<ThemeSelection>(`${this.base}/selection`, update);
  }
}
