import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'r2m.rail.expanded';

/**
 * Nav-rail expanded/collapsed preference, remembered per browser (design §5). Storage failures
 * (private mode, blocked storage) degrade to the in-memory default, never to an error.
 */
@Injectable({ providedIn: 'root' })
export class RailState {
  private readonly _expanded = signal(read());
  readonly expanded = this._expanded.asReadonly();

  toggle(): void {
    this.set(!this._expanded());
  }

  set(expanded: boolean): void {
    this._expanded.set(expanded);
    try {
      localStorage.setItem(STORAGE_KEY, expanded ? '1' : '0');
    } catch {
      // storage unavailable — keep the in-memory value
    }
  }
}

function read(): boolean {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw === null ? true : raw === '1';
  } catch {
    return true;
  }
}
