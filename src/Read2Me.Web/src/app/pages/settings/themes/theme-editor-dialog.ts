import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AppTheme } from '@app/api';
import { contrastText, isHexColour, normalizeHex } from '@app/theme/theme-css';
import {
  DEFAULT_DRAFT,
  OPTIONAL_COLOURS,
  OptionalColourKey,
  ThemeDraft,
  ThemeDraftErrors,
  fromDraft,
  toDraft,
  validateDraft,
} from './theme-form';

export interface ThemeEditorData {
  /** Existing theme to edit; omitted for New. Built-in themes open as a copy. */
  theme?: AppTheme;
}

export type ThemeEditorResult = Omit<AppTheme, 'id' | 'isBuiltIn'>;

type ColourKey = 'primary' | 'secondary' | OptionalColourKey;

/**
 * Theme create/edit form (design §6.4 dialog variant): name, primary/secondary via native colour
 * inputs with a hex readout, dark switch, clearable advanced colours and a live preview strip.
 */
@Component({
  selector: 'app-theme-editor-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSlideToggleModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './theme-editor-dialog.html',
  styleUrl: './theme-editor-dialog.scss',
})
export class ThemeEditorDialog {
  private readonly ref = inject<MatDialogRef<ThemeEditorDialog, ThemeEditorResult>>(MatDialogRef);
  private readonly data = inject<ThemeEditorData>(MAT_DIALOG_DATA, { optional: true }) ?? {};

  readonly isEdit = !!this.data.theme && !this.data.theme.isBuiltIn;
  readonly title = this.isEdit
    ? `Edit ${this.data.theme?.name}`
    : this.data.theme
      ? `Copy of ${this.data.theme.name}`
      : 'New theme';

  readonly draft = signal<ThemeDraft>(
    this.data.theme
      ? {
          ...toDraft(this.data.theme),
          name: this.data.theme.isBuiltIn ? `${this.data.theme.name} copy` : this.data.theme.name,
        }
      : { ...DEFAULT_DRAFT },
  );
  readonly touched = signal<Partial<Record<keyof ThemeDraft, boolean>>>({});
  readonly errors = computed<ThemeDraftErrors>(() => validateDraft(this.draft()));
  readonly valid = computed(() => Object.keys(this.errors()).length === 0);
  readonly optionalColours = OPTIONAL_COLOURS;
  readonly showAdvanced = signal(
    !!this.data.theme && OPTIONAL_COLOURS.some(({ key }) => !!this.data.theme?.[key]),
  );

  /** Preview strip colours: fall back to plausible defaults for blank optionals. */
  readonly preview = computed(() => {
    const d = this.draft();
    const safe = (v: string, fallback: string) => (isHexColour(v) ? normalizeHex(v) : fallback);
    const background = safe(d.background, d.isDark ? '#141518' : '#faf9fd');
    const surface = safe(d.surface, d.isDark ? '#202226' : '#f1f0f4');
    const primary = safe(d.primary, '#594ae2');
    const secondary = safe(d.secondary, '#ff4081');
    const appbar = safe(d.appbarBackground, surface);
    const nav = safe(d.drawerBackground, surface);
    const text = safe(d.textPrimary, contrastText(background));
    return {
      background,
      surface,
      primary,
      secondary,
      appbar,
      appbarText: contrastText(appbar),
      nav,
      navText: contrastText(nav),
      text,
      onPrimary: contrastText(primary),
    };
  });

  error(key: keyof ThemeDraft): string | null {
    return this.touched()[key] ? (this.errors()[key] ?? null) : null;
  }

  setName(value: string): void {
    this.draft.update((d) => ({ ...d, name: value }));
    this.touch('name');
  }

  setDark(value: boolean): void {
    this.draft.update((d) => ({ ...d, isDark: value }));
  }

  setColour(key: ColourKey, value: string): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
    this.touch(key);
  }

  clearColour(key: OptionalColourKey): void {
    this.setColour(key, '');
  }

  /** Native colour inputs only accept #rrggbb; give them a valid value or a neutral fallback. */
  pickerValue(key: ColourKey): string {
    const v = this.draft()[key];
    return isHexColour(v) ? normalizeHex(v) : '#888888';
  }

  toggleAdvanced(): void {
    this.showAdvanced.update((v) => !v);
  }

  save(): void {
    this.touched.set({ name: true, primary: true, secondary: true });
    if (!this.valid()) return;
    this.ref.close(fromDraft(this.draft()));
  }

  cancel(): void {
    this.ref.close();
  }

  private touch(key: keyof ThemeDraft): void {
    this.touched.update((t) => ({ ...t, [key]: true }));
  }
}
