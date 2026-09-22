import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AudioPostProcessStepConfig,
  AudioProcessingApi,
  AudioProcessingFull,
  AudioProcessingUpdateRequest,
  FfmpegProbeResult,
  PauseDurations,
  toApiError,
} from '@app/api';
import { LiveService } from '@app/live/live.service';

/**
 * State behind `/settings/audio` (ticket 24), provided by the page: the one full snapshot every
 * card seeds from. Every write goes to the host and the store reloads from it; the hub's
 * `settingsChanged { area: 'audio-processing' }` reloads it too, so an edit made in Blazor or
 * over the agent API shows up here. Reloads are sequenced: a slow answer never overwrites a
 * newer one.
 */
@Injectable()
export class AudioSettingsStore {
  private readonly api = inject(AudioProcessingApi);
  private readonly live = inject(LiveService);

  private readonly _full = signal<AudioProcessingFull | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly full = this._full.asReadonly();
  readonly error = this._error.asReadonly();

  constructor() {
    this.live
      .on('settingsChanged')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (m.area === 'audio-processing') void this.load();
      });
  }

  async load(): Promise<void> {
    const seq = ++this.loadSeq;
    try {
      const full = await this.api.full();
      if (seq !== this.loadSeq) return;
      this._full.set(full);
      this._error.set(null);
    } catch (e) {
      if (seq !== this.loadSeq) return;
      this._error.set(toApiError(e).message);
    }
  }

  step(stepId: string): AudioPostProcessStepConfig | undefined {
    return this._full()?.steps.find((s) => s.stepId === stepId);
  }

  async saveScalars(patch: AudioProcessingUpdateRequest): Promise<void> {
    await this.api.update(patch);
    await this.load();
  }

  async savePauses(pauses: PauseDurations): Promise<void> {
    await this.api.savePauses(pauses);
    await this.load();
  }

  async saveStep(config: AudioPostProcessStepConfig): Promise<void> {
    await this.api.saveStep(config);
    await this.load();
  }

  /** Persists the path first (so the probe matches the field), then probes. */
  async testFfmpeg(ffmpegPath: string): Promise<FfmpegProbeResult> {
    const result = await this.api.testFfmpeg(ffmpegPath);
    await this.load();
    return result;
  }
}
