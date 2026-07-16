import { savePreference, type PreferenceStorage, type ThemePreference } from "./preferences";

export class ThemeController {
  private readonly onSystemChange = (): void => {
    if (this.preference === "system") this.apply();
  };

  constructor(
    public preference: ThemePreference,
    private readonly storage: PreferenceStorage,
    private readonly systemTheme: MediaQueryList = matchMedia("(prefers-color-scheme: dark)")
  ) {
    this.apply();
    this.systemTheme.addEventListener("change", this.onSystemChange);
  }

  setPreference(preference: ThemePreference): void {
    this.preference = preference;
    savePreference(this.storage, "theme", preference);
    this.apply();
  }

  dispose(): void {
    this.systemTheme.removeEventListener("change", this.onSystemChange);
  }

  private apply(): void {
    const resolved = this.preference === "system" ? (this.systemTheme.matches ? "dark" : "light") : this.preference;
    document.documentElement.dataset.theme = resolved;
    document.documentElement.dataset.themePreference = this.preference;
    document.documentElement.style.colorScheme = resolved;
  }
}
