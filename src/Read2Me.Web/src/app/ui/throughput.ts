import { html, nothing } from 'lit-html';
import type { ThroughputSnapshot } from '@app/live/live-messages';

export interface SparklineOptions {
  values: readonly number[];
  width?: number;
  height?: number;
  stroke?: string;
  label?: string;
}

/** `x,y` pairs scaled to min/max with a 1.5 px inset; a flat series draws a centred line. */
export function sparklinePoints(values: readonly number[], w: number, h: number): string {
  if (values.length === 0) return '';
  const pad = 1.5;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min;
  const stepX = values.length > 1 ? (w - 2 * pad) / (values.length - 1) : 0;
  return values
    .map((v, i) => {
      const x = values.length > 1 ? pad + i * stepX : w / 2;
      const y = span === 0 ? h / 2 : h - pad - ((v - min) / span) * (h - 2 * pad);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
}

/** Inline SVG sparkline over `number[]` (design §7): the throughput trend, nothing more. */
export function sparkline({
  values,
  width = 96,
  height = 24,
  stroke = 'var(--r2m-accent)',
  label = 'Trend',
}: SparklineOptions) {
  const points = sparklinePoints(values, width, height);
  return html`<svg
    class="r2m-sparkline"
    width=${width}
    height=${height}
    viewBox="0 0 ${width} ${height}"
    role="img"
    aria-label=${label}
  >
    ${
      points
        ? html`<polyline
            class="r2m-sparkline__line"
            points=${points}
            stroke=${stroke}
            fill="none"
            stroke-width="1.5"
            stroke-linejoin="round"
            stroke-linecap="round"
          ></polyline>`
        : nothing
    }
  </svg>`;
}

/** The headline for one Throughput Run: live rate while it runs, the run's rate once it ended. */
export function throughputHeadline(t: ThroughputSnapshot): string {
  if (t.isRunActive && t.generationRate != null) return `${t.generationRate.toFixed(1)} tok/s now`;
  if (t.runThroughput != null) return `${t.runThroughput.toFixed(1)} tok/s over the run`;
  return t.isRunActive ? 'Waiting for the first tokens…' : '';
}

const seconds = (ms: number | null | undefined): string =>
  ms == null ? '–' : `${(ms / 1000).toFixed(1)} s`;

/**
 * LLM throughput for one Throughput Run: the live generation rate while it runs, the run's overall
 * rate once it ends, the last ten seconds as a sparkline, and the per-config table after the run.
 * Shared by the activity drawer and the LLM settings test console, so both show the same figures.
 */
export function throughput(snapshot: ThroughputSnapshot) {
  const history = snapshot.generationRateHistory.map((v) => v ?? 0);
  return html`<div class="r2m-throughput">
    <p class="r2m-throughput__headline">${throughputHeadline(snapshot)}</p>
    ${
      history.length
        ? html`<div class="r2m-throughput__sparkline">
            ${sparkline({
              values: history,
              width: 360,
              height: 32,
              label: 'Generation rate, last 10 seconds',
            })}
          </div>`
        : nothing
    }
    ${
      !snapshot.isRunActive && snapshot.perConfig.length
        ? html`<table class="r2m-throughput__table">
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
              ${snapshot.perConfig.map(
                (row) =>
                  html`<tr>
                    <td>${row.configName}</td>
                    <td class="r2m-throughput__num">${row.requests}</td>
                    <td class="r2m-throughput__num">${row.tokensOut ?? '–'}</td>
                    <td class="r2m-throughput__num">${seconds(row.generationMs)}</td>
                    <td class="r2m-throughput__num">${row.tokensPerSecond?.toFixed(1) ?? '–'}</td>
                  </tr>`,
              )}
            </tbody>
          </table>`
        : nothing
    }
  </div>`;
}
