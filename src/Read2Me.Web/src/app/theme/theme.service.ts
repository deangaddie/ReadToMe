import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { AppTheme, ThemeSelection, ThemesApi } from '@app/api';
import { buildThemeStylesheet, resolveEffectiveTheme } from './theme-css';

const STYLE_ID = 'r2m-theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';

export type SchemePreference = 'light' | 'dark' | 'system';

/**
 * Runtime theming (spec D9, design §3): loads the shared theme store from the host, resolves the
 * effective AppTheme (following the OS preference live when asked), and applies it as CSS custom
 * properties in a `<style id="r2m-theme">` element plus `data-theme` on `<html>` (which styles.scss
 * maps to `color-scheme`). Both UIs read the same rows, so applying here restyles Blazor too.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly api = inject(ThemesApi);
  private readonly doc = inject(DOCUMENT);

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

  /** Called once from the app initializer; a failure keeps the compiled defaults and records why. */
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
    const win = this.doc.defaultView;
    if (!win || typeof win.matchMedia !== 'function') return;
    const query = win.matchMedia(DARK_QUERY);
    this._prefersDark.set(query.matches);
    query.addEventListener('change', (event) => this._prefersDark.set(event.matches));
  }

  private apply(theme: AppTheme | null): void {
    const root = this.doc.documentElement;
    let style = this.doc.getElementById(STYLE_ID) as HTMLStyleElement | null;
    if (!theme) {
      delete root.dataset['theme'];
      style?.remove();
      return;
    }
    root.dataset['theme'] = theme.isDark ? 'dark' : 'light';
    if (!style) {
      style = this.doc.createElement('style');
      style.id = STYLE_ID;
      this.doc.head.appendChild(style);
    }
    style.textContent = buildThemeStylesheet(theme);
  }
}
