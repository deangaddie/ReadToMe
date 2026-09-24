import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ThroughputSnapshot } from '@app/live/live-messages';
import { Sparkline } from '@app/ui/sparkline/sparkline';

/**
 * LLM throughput for one Throughput Run: the live generation rate while it runs, the run's overall
 * rate once it ends, the last ten seconds as a sparkline, and the per-config table after the run.
 * Shared by the activity drawer and the LLM settings test console, so both show the same figures.
 */
@Component({
  selector: 'r2m-throughput',
  imports: [Sparkline],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-throughput' },
  template: `
    <p class="r2m-throughput__headline">{{ headline() }}</p>
    @if (history().length) {
      <r2m-sparkline
        class="r2m-throughput__sparkline"
        [values]="history()"
        [width]="360"
        [height]="32"
        label="Generation rate, last 10 seconds"
      />
    }
    @if (!snapshot().isRunActive && snapshot().perConfig.length) {
      <table class="r2m-throughput__table">
        <thead>
          <tr>
            <th scope="col">Config</th>
            <th scope="col" class="r2m-throughput__num">Requests</th>
            <th scope="col" class="r2m-throughput__num">Tokens out</th>
            <th scope="col" class="r2m-throughput__num">Gen time</th>
            <th scope="col" class="r2m-throughput__num">tok/s</th>
          </tr>
        </thead>
        <tbody>
          @for (row of snapshot().perConfig; track row.configId) {
            <tr>
              <td>{{ row.configName }}</td>
              <td class="r2m-throughput__num">{{ row.requests }}</td>
              <td class="r2m-throughput__num">{{ row.tokensOut ?? '–' }}</td>
              <td class="r2m-throughput__num">{{ seconds(row.generationMs) }}</td>
              <td class="r2m-throughput__num">{{ row.tokensPerSecond?.toFixed(1) ?? '–' }}</td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .r2m-throughput__headline {
      margin: 0;
      font-variant-numeric: tabular-nums;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-throughput__sparkline {
      display: block;
      width: 100%;
      margin-top: var(--r2m-space-2);
    }
    .r2m-throughput__sparkline ::ng-deep svg {
      width: 100%;
      height: 32px;
    }
    .r2m-throughput__table {
      width: 100%;
      margin-top: var(--r2m-space-2);
      border-collapse: collapse;
      font-size: var(--r2m-text-sm);
    }
    .r2m-throughput__table th,
    .r2m-throughput__table td {
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border-bottom: 1px solid var(--r2m-outline);
      text-align: left;
    }
    .r2m-throughput__table th {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .r2m-throughput__num {
      text-align: right !important;
      font-variant-numeric: tabular-nums;
    }
  `,
})
export class Throughput {
  readonly snapshot = input.required<ThroughputSnapshot>();

  protected readonly history = computed(() =>
    this.snapshot().generationRateHistory.map((v) => v ?? 0),
  );

  protected readonly headline = computed(() => {
    const t = this.snapshot();
    if (t.isRunActive && t.generationRate != null)
      return `${t.generationRate.toFixed(1)} tok/s now`;
    if (t.runThroughput != null) return `${t.runThroughput.toFixed(1)} tok/s over the run`;
    return t.isRunActive ? 'Waiting for the first tokens…' : '';
  });

  protected seconds(ms: number | null | undefined): string {
    return ms == null ? '–' : `${(ms / 1000).toFixed(1)} s`;
  }
}
