import {
  type AudioPostProcessStepConfig,
  AudioProcessingApi,
  type AudioProcessingFullResponse,
  type AudioProcessingUpdateRequest,
  type FfmpegProbeResult,
  type PauseDurationsDto,
  toApiError,
} from '@app/api';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { LiveService, type Unsubscribe } from '@app/live/live.service';

/**
 * State behind `/settings/audio` (ticket 24), provided by the page: the one full snapshot every
 * card seeds from. Every write goes to the host and the store reloads from it; the hub's
 * `settingsChanged { area: 'audio-processing' }` reloads it too, so an edit made in another tab
 * or over the agent API shows up here. Reloads are sequenced: a slow answer never overwrites a
 * newer one. The page calls {@link open} on connect and {@link close} on disconnect.
 */
export class AudioSettingsStore {
  private readonly api = use(AudioProcessingApi);
  private readonly live = use(LiveService);
  private subscription: Unsubscribe | null = null;

  private readonly _full = signal<AudioProcessingFullResponse | null>(null);
  private readonly _error = signal<string | null>(null);
  private loadSeq = 0;

  /** Null until the first load lands. */
  readonly full = this._full.asReadonly();
  readonly error = this._error.asReadonly();

  /** Follows the hub's `settingsChanged` for the area and loads; called on every connect. */
  open(): Promise<void> {
    this.subscription?.();
    this.subscription = this.live.on('settingsChanged', (m) => {
      if (m.area === 'audio-processing') void this.load();
    });
    return this.load();
  }

  /** Drops the hub subscription and any load still in flight. */
  close(): void {
    this.subscription?.();
    this.subscription = null;
    this.loadSeq++;
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

  async savePauses(pauses: PauseDurationsDto): Promise<void> {
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
