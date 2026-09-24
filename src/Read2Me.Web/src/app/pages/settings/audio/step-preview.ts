import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  inject,
  input,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AudioProcessingApi, RecentAudioSample, StepPreviewResponse, toApiError } from '@app/api';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { ToastService } from '@app/ui/toast/toast.service';
import { SamplePicker, provenance, snippet } from './sample-picker-dialog';

/**
 * A post-process card's A/B preview (ticket 24): pick a recent sample, render the card's
 * *unsaved* settings over its Preview Source, then hear off-vs-draft on one A/B player. Renders
 * are on demand — the draft is only pushed through ffmpeg when asked, never per keystroke — and
 * a new sample drops the parked render rather than pairing it with the wrong original.
 */
@Component({
  selector: 'app-step-preview',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, AudioPlayer],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'step-preview' },
  template: `
    <div class="step-preview__head">
      <span class="step-preview__title">A/B preview</span>
      @if (sample(); as sample) {
        <span class="step-preview__sample" data-role="sample">
          <span class="step-preview__snippet">{{ snippet(sample.text) }}</span>
          <span class="step-preview__meta">{{ provenance(sample) }}</span>
        </span>
      }
      <span class="step-preview__actions">
        <button
          mat-stroked-button
          type="button"
          data-action="pick-sample"
          [disabled]="rendering()"
          (click)="pick()"
        >
          {{ sample() ? 'Change sample' : 'Pick sample' }}
        </button>
        <button
          mat-flat-button
          type="button"
          data-action="render-preview"
          [disabled]="!sample() || rendering() || !canRender()"
          (click)="render()"
        >
          @if (rendering()) {
            <mat-progress-spinner mode="indeterminate" diameter="16" />
            Rendering…
          } @else {
            Render preview
          }
        </button>
      </span>
    </div>

    @if (result(); as result) {
      <r2m-audio-player
        [src]="result.originalUrl"
        [srcB]="result.processedUrl"
        [cacheKey]="result.previewId"
        labelA="Original"
        [labelB]="processedLabel()"
        [label]="sample()?.characterName ?? 'Narration'"
      />
      @if (showRemovedMs() && result.removedMs !== null) {
        <p class="step-preview__note" data-role="removed">Removed {{ removedLabel(result) }} ms</p>
      }
      @if (!result.appliedOk) {
        <p class="step-preview__reason" role="alert" data-role="reason">
          <mat-icon>info</mat-icon>
          {{ result.reason ?? 'The step fell back to the unprocessed audio.' }}
        </p>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      margin-top: var(--r2m-space-3);
      padding: var(--r2m-space-3);
      border: 1px dashed var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
    }
    .step-preview__head {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-2) var(--r2m-space-3);
    }
    .step-preview__title {
      font-weight: 600;
      font-size: var(--r2m-text-sm);
    }
    .step-preview__sample {
      display: flex;
      flex-direction: column;
      min-width: 0;
      flex: 1 1 200px;
    }
    .step-preview__snippet {
      font-size: var(--r2m-text-sm);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .step-preview__meta,
    .step-preview__note {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
    }
    .step-preview__actions {
      display: flex;
      gap: var(--r2m-space-2);
      margin-left: auto;
    }
    .step-preview__note,
    .step-preview__reason {
      margin: 0;
    }
    .step-preview__reason {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-warn);
    }
    .step-preview__reason mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    mat-progress-spinner {
      display: inline-block;
      margin-right: var(--r2m-space-1);
    }
  `,
})
export class StepPreview {
  private readonly api = inject(AudioProcessingApi);
  private readonly picker = inject(SamplePicker);
  private readonly toast = inject(ToastService);

  readonly stepId = input.required<string>();
  /** The card's unsaved settings payload — what a render sends. */
  readonly settings = input.required<Record<string, unknown> | null>();
  readonly processedLabel = input('Processed');
  /** Cards that trim report how much audio the step removed. */
  readonly showRemovedMs = input(false, { transform: booleanAttribute });
  /** False while the card's draft is invalid: nothing to audition yet. */
  readonly canRender = input(true);

  readonly sample = signal<RecentAudioSample | null>(null);
  readonly result = signal<StepPreviewResponse | null>(null);
  readonly rendering = signal(false);

  protected readonly snippet = snippet;
  protected readonly provenance = provenance;

  protected removedLabel(result: StepPreviewResponse): string {
    return Math.round(result.removedMs ?? 0).toString();
  }

  protected async pick(): Promise<void> {
    const picked = await this.picker.pick();
    if (!picked) return;
    this.sample.set(picked);
    // The parked preview belongs to the previous sample.
    this.result.set(null);
  }

  protected async render(): Promise<void> {
    const sample = this.sample();
    if (!sample || this.rendering()) return;
    this.rendering.set(true);
    try {
      this.result.set(
        await this.api.previewStep(this.stepId(), {
          sample: { folder: sample.folder, itemId: sample.itemId },
          settings: this.settings(),
        }),
      );
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.rendering.set(false);
    }
  }
}
