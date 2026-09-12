import { ChangeDetectionStrategy, Component } from '@angular/core';

interface TokenRow {
  name: string;
  note: string;
}

const STATUS: TokenRow[] = [
  { name: '--r2m-status-ok', note: 'done, ready, verified' },
  { name: '--r2m-status-warn', note: 'needs review, degraded' },
  { name: '--r2m-status-error', note: 'failed, down' },
  { name: '--r2m-status-info', note: 'queued, informational' },
  { name: '--r2m-status-busy', note: 'running, loading' },
  { name: '--r2m-status-neutral', note: 'not started, idle' },
];

const SURFACES: TokenRow[] = [
  { name: '--r2m-accent', note: 'primary action colour' },
  { name: '--r2m-appbar-bg', note: 'app bar' },
  { name: '--r2m-nav-bg', note: 'nav rail' },
  { name: '--r2m-nav-active-bg', note: 'active rail item' },
  { name: '--r2m-activity-bg', note: 'activity bar' },
  { name: '--r2m-surface', note: 'page' },
  { name: '--r2m-surface-low', note: 'drawer, cards' },
  { name: '--r2m-surface-high', note: 'raised chips' },
  { name: '--r2m-outline', note: 'dividers' },
];

const SPACING = [1, 2, 3, 4, 5, 6, 8, 10];
const RADII = ['sm', 'md', 'lg', 'pill'];
const TEXT = ['xs', 'sm', 'md', 'lg', 'xl', '2xl'];

/** Design-token table (design §10): swatches read their own CSS variable, so they follow the scheme. */
@Component({
  selector: 'app-tokens-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'tokens' },
  template: `
    <h3>Status colours</h3>
    <p class="tokens__note">Fixed per scheme; themes never override these (design §3.3).</p>
    <table class="tokens__table">
      <thead>
        <tr>
          <th>Token</th>
          <th>Strong</th>
          <th>Soft</th>
          <th>Use</th>
        </tr>
      </thead>
      <tbody>
        @for (row of status; track row.name) {
          <tr>
            <td>
              <code>{{ row.name }}</code>
            </td>
            <td>
              <span class="tokens__swatch" [style.background]="'var(' + row.name + ')'"></span>
            </td>
            <td>
              <span class="tokens__swatch" [style.background]="'var(' + row.name + '-soft)'"></span>
            </td>
            <td>{{ row.note }}</td>
          </tr>
        }
      </tbody>
    </table>

    <h3>Surfaces</h3>
    <table class="tokens__table">
      <thead>
        <tr>
          <th>Token</th>
          <th>Swatch</th>
          <th>Use</th>
        </tr>
      </thead>
      <tbody>
        @for (row of surfaces; track row.name) {
          <tr>
            <td>
              <code>{{ row.name }}</code>
            </td>
            <td>
              <span class="tokens__swatch" [style.background]="'var(' + row.name + ')'"></span>
            </td>
            <td>{{ row.note }}</td>
          </tr>
        }
      </tbody>
    </table>

    <h3>Spacing (4-pt grid)</h3>
    <div class="tokens__row">
      @for (n of spacing; track n) {
        <div class="tokens__space">
          <span class="tokens__bar" [style.width]="'var(--r2m-space-' + n + ')'"></span>
          <code>--r2m-space-{{ n }}</code>
        </div>
      }
    </div>

    <h3>Radii</h3>
    <div class="tokens__row">
      @for (r of radii; track r) {
        <div class="tokens__radius" [style.border-radius]="'var(--r2m-radius-' + r + ')'">
          <code>{{ r }}</code>
        </div>
      }
    </div>

    <h3>Typography</h3>
    <div class="tokens__type">
      @for (t of text; track t) {
        <p [style.font-size]="'var(--r2m-text-' + t + ')'">
          --r2m-text-{{ t }} — The quick brown fox jumps over the lazy dog
        </p>
      }
      <p class="tokens__mono">--r2m-font-mono — {{ '{' }} "prompt": "…" {{ '}' }}</p>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    h3 {
      margin: var(--r2m-space-6) 0 var(--r2m-space-2);
      font-size: var(--r2m-text-md);
      font-weight: 600;
    }
    .tokens__note {
      margin: 0 0 var(--r2m-space-2);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .tokens__table {
      border-collapse: collapse;
      width: 100%;
      font-size: var(--r2m-text-sm);
    }
    .tokens__table th,
    .tokens__table td {
      text-align: left;
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border-bottom: 1px solid var(--r2m-outline);
    }
    .tokens__swatch {
      display: inline-block;
      width: 40px;
      height: 20px;
      border-radius: var(--r2m-radius-sm);
      border: 1px solid var(--r2m-outline);
    }
    .tokens__row {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-4);
      align-items: flex-end;
    }
    .tokens__space {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
      font-size: var(--r2m-text-xs);
    }
    .tokens__bar {
      display: block;
      height: 12px;
      background: var(--r2m-accent);
    }
    .tokens__radius {
      width: 64px;
      height: 40px;
      display: grid;
      place-items: center;
      border: 2px solid var(--r2m-accent);
      font-size: var(--r2m-text-xs);
    }
    .tokens__type p {
      margin: var(--r2m-space-1) 0;
    }
    .tokens__mono {
      font-family: var(--r2m-font-mono);
    }
  `,
})
export class TokensTable {
  readonly status = STATUS;
  readonly surfaces = SURFACES;
  readonly spacing = SPACING;
  readonly radii = RADII;
  readonly text = TEXT;
}
