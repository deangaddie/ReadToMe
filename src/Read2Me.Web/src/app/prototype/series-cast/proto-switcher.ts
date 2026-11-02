/**
 * PROTOTYPE — throwaway (#208). Floating bar: ←/→ cycle the variant (?variant=A|B|C), a scenario picker,
 * and the last few actions so every change is visible.
 */
import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import * as S from './proto-store';

const KEYS = Object.keys(S.VARIANTS) as S.Variant[];

export class ProtoSeriesCastSwitcher extends R2mElement {
  protected override connected(): void {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest('input, textarea, select, [contenteditable], dialog')) return;
      if (e.key === 'ArrowLeft') this.cycle(-1);
      if (e.key === 'ArrowRight') this.cycle(1);
    };
    addEventListener('keydown', onKey);
    this.onDisconnect(() => removeEventListener('keydown', onKey));
  }

  private cycle(d: number) {
    const i = KEYS.indexOf(S.variant());
    S.setVariant(KEYS[(i + d + KEYS.length) % KEYS.length]!);
  }

  protected template() {
    const v = S.variant();
    return html`<div class="sx-switcher">
      <div class="sx-switcher__main">
        <button type="button" aria-label="Previous variant" @click=${() => this.cycle(-1)}>
          ◀
        </button>
        <span class="sx-switcher__label"><strong>${v}</strong> ${S.VARIANTS[v]}</span>
        <button type="button" aria-label="Next variant" @click=${() => this.cycle(1)}>▶</button>
        <select
          aria-label="Scenario"
          @change=${(e: Event) => S.setScenario((e.target as HTMLSelectElement).value as S.Scenario)}
        >
          ${(Object.keys(S.SCENARIOS) as S.Scenario[]).map(
            (s) =>
              html`<option value=${s} ?selected=${s === S.scenario()}>${S.SCENARIOS[s]}</option>`,
          )}
        </select>
      </div>
      <ol class="sx-switcher__log">
        ${S.actions().map((a) => html`<li>${a}</li>`)}
      </ol>
    </div>`;
  }
}
define('r2m-proto-series-cast-switcher', ProtoSeriesCastSwitcher);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-series-cast-switcher': ProtoSeriesCastSwitcher;
  }
}
