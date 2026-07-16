import type { RefreshSeconds } from "./readiness-controller";

export type ThemePreference = "system" | "light" | "dark";
export type PreferenceName = "refresh" | "theme";

export interface PreferenceStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface DashboardPreferences {
  readonly refreshSeconds: RefreshSeconds;
  readonly theme: ThemePreference;
}

const keys: Record<PreferenceName, string> = {
  refresh: "chd.refresh",
  theme: "chd.theme"
};

const refreshValues = new Set(["2", "10", "30"]);
const themeValues = new Set(["system", "light", "dark"]);

function readAllowed(storage: PreferenceStorage, name: PreferenceName, allowed: ReadonlySet<string>, fallback: string): string {
  const key = keys[name];
  try {
    const value = storage.getItem(key);
    if (value === null) return fallback;
    if (allowed.has(value)) return value;
    try { storage.removeItem(key); } catch { /* storage is optional */ }
  } catch { /* storage is optional */ }
  return fallback;
}

export function loadPreferences(storage: PreferenceStorage): DashboardPreferences {
  return {
    refreshSeconds: Number(readAllowed(storage, "refresh", refreshValues, "10")) as RefreshSeconds,
    theme: readAllowed(storage, "theme", themeValues, "system") as ThemePreference
  };
}

export function savePreference(storage: PreferenceStorage, name: PreferenceName, value: string): void {
  const allowed = name === "refresh" ? refreshValues : themeValues;
  if (!allowed.has(value)) return;
  try { storage.setItem(keys[name], value); } catch { /* page-only preferences are acceptable */ }
}
