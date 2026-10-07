import { type AppTheme, type ThemeSelection, ThemesApi } from '@app/api';
import { use } from '@app/core/services';
import { computed, effect, signal } from '@app/core/signals';
import { buildThemeStylesheet, resolveEffectiveTheme } from './theme-css';

const STYLE_ID = 'r2m-theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';

export type SchemePreference = 'light' | 'dark' | 'system';

/**
 * Runtime theming: loads the shared theme store from the host, resolves the effective AppTheme
 * (following the OS preference live when asked), and applies it as CSS custom properties in a
 * `<style id="r2m-theme">` element plus `data-theme` on `<html>` (which global.css maps to
 * `color-scheme`). The host stores the selection, so every client loads the same theme.
 */
export class ThemeService {
  private readonly api = use(ThemesApi);

  private readonly _themes = signal<AppTheme[]>([]);
  private readonly _selection = signal<ThemeSelection | null>(null);
  private readonly _prefersDark = signal(false);
  private readonly _loaded = signal(false);
  private readonly _error = signal<string | null>(null);

  readonly themes = this._themes.asReadonly();
  readonly selection = this._selection.asReadonly();
  readonly prefersDark = this._prefersDark.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly error = this._error.asReadonly();

  readonly effective = computed(() =>
    resolveEffectiveTheme(this._themes(), this._selection(), this._prefersDark()),
  );
  readonly followSystem = computed(() => this._selection()?.followSystemPreference ?? false);
  readonly preference = computed<SchemePreference>(() =>
    this.followSystem() ? 'system' : this.effective()?.isDark ? 'dark' : 'light',
  );
  /** The theme marked Active on the themes page: the selected row, never the follow-system pick. */
  readonly selectedId = computed(() => this._selection()?.selectedThemeId ?? null);

  constructor() {
    this.watchSystemPreference();
    effect(() => this.apply(this.effective()));
  }

  /** Called once on boot; a failure keeps the compiled defaults and records why. */
  async load(): Promise<void> {
    try {
      const [themes, selection] = await Promise.all([this.api.list(), this.api.selection()]);
      this._themes.set(themes);
      this._selection.set(selection);
      this._error.set(null);
    } catch (error) {
      this._error.set(error instanceof Error ? error.message : String(error));
    } finally {
      this._loaded.set(true);
    }
  }

  async applyTheme(id: number): Promise<void> {
    this._selection.set(
      await this.api.setSelection({ selectedThemeId: id, followSystemPreference: false }),
    );
  }

  async setFollowSystem(follow: boolean): Promise<void> {
    this._selection.set(await this.api.setSelection({ followSystemPreference: follow }));
  }

  /** App-bar quick menu: Light / Dark go to the built-in rows, System flips the follow flag. */
  async setPreference(preference: SchemePreference): Promise<void> {
    if (preference === 'system') {
      await this.setFollowSystem(true);
      return;
    }
    const row = this.builtIn(preference);
    if (row) await this.applyTheme(row.id);
  }

  async create(theme: Omit<AppTheme, 'id' | 'isBuiltIn'>): Promise<AppTheme> {
    const created = await this.api.create(theme);
    await this.reloadThemes();
    return created;
  }

  async update(theme: AppTheme): Promise<AppTheme> {
    const updated = await this.api.update(theme);
    await this.reloadThemes();
    return updated;
  }

  async delete(id: number): Promise<void> {
    await this.api.delete(id);
    // The host clears the selection when the active theme goes; mirror it without a second call.
    this._selection.update((s) =>
      s && s.selectedThemeId === id ? { ...s, selectedThemeId: null } : s,
    );
    await this.reloadThemes();
  }

  private builtIn(scheme: 'light' | 'dark'): AppTheme | undefined {
    const themes = this._themes();
    const wantDark = scheme === 'dark';
    return (
      themes.find((t) => t.isBuiltIn && t.name === (wantDark ? 'Dark' : 'Light')) ??
      themes.find((t) => t.isBuiltIn && t.isDark === wantDark)
    );
  }

  private async reloadThemes(): Promise<void> {
    this._themes.set(await this.api.list());
  }

  private watchSystemPreference(): void {
    if (typeof matchMedia !== 'function') return;
    const query = matchMedia(DARK_QUERY);
    this._prefersDark.set(query.matches);
    query.addEventListener('change', (event) => this._prefersDark.set(event.matches));
  }

  private apply(theme: AppTheme | null): void {
    const root = document.documentElement;
    let style = document.getElementById(STYLE_ID) as HTMLStyleElement | null;
    if (!theme) {
      delete root.dataset['theme'];
      style?.remove();
      return;
    }
    root.dataset['theme'] = theme.isDark ? 'dark' : 'light';
    if (!style) {
      style = document.createElement('style');
      style.id = STYLE_ID;
      document.head.appendChild(style);
    }
    style.textContent = buildThemeStylesheet(theme);
  }
}
