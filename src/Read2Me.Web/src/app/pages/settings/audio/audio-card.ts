import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

/**
 * One independently saved card of the audio-processing page (ticket 24): title, blurb,
 * projected fields, then a footer with the validation message, extra actions (`[actions]`)
 * and a Save gated on dirty + valid.
 */
@Component({
  selector: 'app-audio-card',
  imports: [MatButtonModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'audio-card', '[attr.data-card]': 'card()' },
  template: `
    <header class="audio-card__head">
      <h2 class="audio-card__title">{{ title() }}</h2>
      @if (dirty()) {
        <span class="audio-card__dirty" role="status">Unsaved changes</span>
      }
    </header>
    <p class="audio-card__blurb">{{ blurb() }}</p>
    <div class="audio-card__body">
      <ng-content />
    </div>
    <footer class="audio-card__foot">
      @if (error(); as error) {
        <span class="audio-card__error" role="alert" data-role="card-error">{{ error }}</span>
      }
      <span class="audio-card__actions">
        <ng-content select="[actions]" />
        <button
          mat-flat-button
          type="button"
          data-action="save"
          [disabled]="!dirty() || !!error() || saving()"
          (click)="save.emit()"
        >
          @if (saving()) {
            <mat-progress-spinner mode="indeterminate" diameter="16" />
            Saving…
          } @else {
            Save
          }
        </button>
      </span>
    </footer>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
    }
    .audio-card__head {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--r2m-space-3);
    }
    .audio-card__title {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .audio-card__dirty {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-status-warn);
    }
    .audio-card__blurb {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .audio-card__body {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .audio-card__foot {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
      margin-top: var(--r2m-space-1);
    }
    .audio-card__error {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-error);
    }
    .audio-card__actions {
      display: flex;
      gap: var(--r2m-space-2);
      margin-left: auto;
    }
    mat-progress-spinner {
      display: inline-block;
      margin-right: var(--r2m-space-1);
    }
  `,
})
export class AudioCard {
  /** Stable id for tests and the E2E driver. */
  readonly card = input.required<string>();
  readonly title = input.required<string>();
  readonly blurb = input('');
  readonly dirty = input(false);
  readonly saving = input(false);
  /** The card's validation message; Save is disabled while set. */
  readonly error = input<string | null>(null);

  readonly save = output<void>();
}
