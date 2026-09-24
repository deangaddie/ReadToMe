import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  AudioPostProcessStepConfig,
  AudioProcessingFull,
  AudioProcessingSettings,
  AudioProcessingUpdateRequest,
  FfmpegProbeResult,
  FfmpegTestRequest,
  PauseDurations,
  RecentAudioSample,
  StepPreviewRequest,
  StepPreviewResponse,
} from './dtos';

/**
 * `AudioProcessingEndpoints.cs`: the single audio post-processing row — scalars, assembler pauses,
 * the paragraph step configs — plus the ffmpeg probe, the recent-sample picker and the one-step
 * A/B preview (ticket 24).
 */
@Injectable({ providedIn: 'root' })
export class AudioProcessingApi {
  private readonly api = inject(ApiClient);
  private readonly base = '/api/settings/audio-processing';

  get(): Promise<AudioProcessingSettings> {
    return this.api.get<AudioProcessingSettings>(this.base);
  }

  /** Only the supplied scalars change; the full row comes back. */
  update(patch: AudioProcessingUpdateRequest): Promise<AudioProcessingSettings> {
    return this.api.put<AudioProcessingSettings>(this.base, patch);
  }

  /** Scalars + pauses + step configs in one read. */
  full(): Promise<AudioProcessingFull> {
    return this.api.get<AudioProcessingFull>(`${this.base}/full`);
  }

  /** All five pauses together. 400 on a negative value. */
  savePauses(pauses: PauseDurations): Promise<PauseDurations> {
    return this.api.put<PauseDurations>(`${this.base}/pauses`, pauses);
  }

  /** One step's enabled flag + settings; the other step keeps its config. */
  saveStep(config: AudioPostProcessStepConfig): Promise<AudioPostProcessStepConfig> {
    return this.api.put<AudioPostProcessStepConfig>(
      `${this.base}/steps/${encodeURIComponent(config.stepId)}`,
      config,
    );
  }

  /** Persists the path, then probes it — the same order as Blazor's Test button. */
  testFfmpeg(ffmpegPath: string | null): Promise<FfmpegProbeResult> {
    const request: FfmpegTestRequest = { ffmpegPath };
    return this.api.post<FfmpegProbeResult>(`${this.base}/ffmpeg/test`, request);
  }

  /** Newest first; only items whose Preview Source is still cached. */
  recentSamples(limit = 20): Promise<RecentAudioSample[]> {
    return this.api.get<RecentAudioSample[]>('/api/audio/samples/recent', { limit });
  }

  /** Render one step with unsaved settings over a sample. 404 unknown folder, 422 evicted sample. */
  previewStep(stepId: string, request: StepPreviewRequest): Promise<StepPreviewResponse> {
    return this.api.post<StepPreviewResponse>(
      `${this.base}/steps/${encodeURIComponent(stepId)}/preview`,
      request,
    );
  }
}
