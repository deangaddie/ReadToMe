import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { NarratorDto } from '@app/api';

/**
 * What the detail shows when the Narrator row is selected while the book is narrated by a
 * character (research §4 "Narrator signpost"): where narration is really edited, a button to go
 * there, and the seed Narrator's own voices, kept unused until the link is removed.
 */
@Component({
  selector: 'app-narrator-signpost',
  imports: [MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'narrator-signpost', 'data-testid': 'narrator-signpost' },
  template: `
    <h2 class="narrator-signpost__title">
      <mat-icon aria-hidden="true">link</mat-icon> Narrator → {{ narrator().displayName }}
    </h2>
    <p class="narrator-signpost__info">
      Narration in this book is spoken by {{ narrator().displayName }}. Voices and voice rules are
      edited on {{ narrator().displayName }}.
    </p>
    <button mat-flat-button type="button" (click)="goToLinked.emit()">
      <mat-icon>arrow_forward</mat-icon> Go to {{ narrator().displayName }}
    </button>
    @if (unusedVoices().length > 0) {
      <details class="narrator-signpost__unused">
        <summary>
          {{ unusedVoices().length }} unused narrator
          {{ unusedVoices().length === 1 ? 'voice' : 'voices' }}
        </summary>
        <p class="narrator-signpost__hint">
          These voices are kept safely and return when the narrator link is removed.
        </p>
        <ul>
          @for (name of unusedVoices(); track $index) {
            <li>{{ name }}</li>
          }
        </ul>
      </details>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      align-items: flex-start;
    }
    .narrator-signpost__title {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      margin: 0;
      font-size: var(--r2m-text-xl);
      font-weight: 500;
    }
    .narrator-signpost__info {
      margin: 0;
      padding: var(--r2m-space-3);
      border: 1px solid var(--r2m-status-info);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-status-info-soft);
    }
    .narrator-signpost__unused {
      width: 100%;
    }
    .narrator-signpost__unused summary {
      cursor: pointer;
      font-weight: 500;
    }
    .narrator-signpost__hint {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
  `,
})
export class NarratorSignpost {
  readonly narrator = input.required<NarratorDto>();
  /** The seed Narrator row's own voice names. */
  readonly unusedVoices = input<readonly string[]>([]);
  readonly goToLinked = output<void>();
}
