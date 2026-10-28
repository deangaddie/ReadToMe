/**
 * PROTOTYPE — throwaway. Floating bar: ←/→ cycle the variant, a scenario picker, and
 * "Simulate app start" to replay first-load surfacing. Dev server only.
 */
import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import * as S from './proto-store';
import { startToastC } from './proto-views';

const KEYS = Object.keys(S.VARIANTS) as S.Variant[];

export class ProtoSwitcher extends R2mElement {
  private readonly router = use(Router);

  protected override connected(): void {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest('input, textarea, select, [contenteditable]')) return;
      if (e.key === 'ArrowLeft') this.cycle(-1);
      if (e.key === 'ArrowRight') this.cycle(1);
    };
    addEventListener('keydown', onKey);
    this.onDisconnect(() => removeEventListener('keydown', onKey));
    queueMicrotask(() => this.afterStart());
  }

  private cycle(d: number) {
    const i = KEYS.indexOf(S.variant());
    S.setVariant(KEYS[(i + d + KEYS.length) % KEYS.length]!);
    this.afterStart();
  }

  /** What an app start does in each variant beyond what the shell renders reactively. */
  private afterStart(simulate = true) {
    if (simulate) S.simulateStart();
    if (S.variant() === 'C' && S.firstRun()) this.router.navigate('setup');
    startToastC();
  }

  protected template() {
    const v = S.variant();
    return html`<div class="pc-switcher">
      <button type="button" aria-label="Previous variant" @click=${() => this.cycle(-1)}>◀</button>
      <span class="pc-switcher__label"><strong>${v}</strong> ${S.VARIANTS[v]}</span>
      <button type="button" aria-label="Next variant" @click=${() => this.cycle(1)}>▶</button>
      <select
        aria-label="Scenario"
        @change=${(e: Event) => {
          S.setScenario((e.target as HTMLSelectElement).value as S.Scenario);
          this.afterStart(false);
        }}
      >
        ${(Object.keys(S.SCENARIOS) as S.Scenario[]).map(
          (s) => html`<option value=${s} ?selected=${s === S.scenario()}>${S.SCENARIOS[s]}</option>`,
        )}
      </select>
      <button type="button" @click=${() => this.afterStart()}>Simulate app start</button>
    </div>`;
  }
}
define('r2m-proto-switcher', ProtoSwitcher);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-switcher': ProtoSwitcher;
  }
}
