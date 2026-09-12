import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Inline SVG sparkline over `number[]` (design §7): the throughput trend, nothing more. */
@Component({
  selector: 'r2m-sparkline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-sparkline' },
  template: `
    <svg
      class="r2m-sparkline__svg"
      [attr.width]="width()"
      [attr.height]="height()"
      [attr.viewBox]="'0 0 ' + width() + ' ' + height()"
      role="img"
      [attr.aria-label]="label()"
    >
      @if (points()) {
        <polyline
          class="r2m-sparkline__line"
          [attr.points]="points()"
          [attr.stroke]="stroke()"
          fill="none"
          stroke-width="1.5"
          stroke-linejoin="round"
          stroke-linecap="round"
        />
      }
    </svg>
  `,
  styles: `
    :host {
      display: inline-block;
      line-height: 0;
    }
  `,
})
export class Sparkline {
  readonly values = input.required<number[]>();
  readonly width = input(96);
  readonly height = input(24);
  readonly stroke = input('var(--r2m-accent)');
  readonly label = input('Trend');

  /** `x,y` pairs scaled to min/max with a 1.5 px inset; a flat series draws a centred line. */
  protected readonly points = computed(() => {
    const values = this.values();
    if (values.length === 0) return '';
    const w = this.width();
    const h = this.height();
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
  });
}
