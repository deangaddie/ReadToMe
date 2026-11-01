import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  AudioPostProcessStepConfig,
  AudioProcessingFullResponse,
  AudioProcessingSettings,
  AudioProcessingUpdateRequest,
  FfmpegProbeResult,
  PauseDurationsDto,
  RecentAudioSampleDto,
  StepPreviewRequest,
  StepPreviewResponse,
} from './dtos';

/**
 * `AudioProcessingEndpoints.cs`: the single audio post-processing row — scalars, assembler pauses,
 * the paragraph step configs — plus the ffmpeg probe, the recent-sample picker and the one-step
 * A/B preview (ticket 24). The export page reads the scalar row for its readiness card.
 */
export class AudioProcessingApi {
  private readonly api = use(ApiClient);

  get(): Promise<AudioProcessingSettings> {
    return this.api.get('/api/settings/audio-processing');
  }

  /** Only the supplied scalars change; the full row comes back. */
  update(patch: AudioProcessingUpdateRequest): Promise<AudioProcessingSettings> {
    return this.api.put('/api/settings/audio-processing', { body: patch });
  }

  /** Scalars + pauses + step configs in one read. */
  full(): Promise<AudioProcessingFullResponse> {
    return this.api.get('/api/settings/audio-processing/full');
  }

  /** All five pauses together. 400 on a negative value. */
  savePauses(pauses: PauseDurationsDto): Promise<PauseDurationsDto> {
    return this.api.put('/api/settings/audio-processing/pauses', { body: pauses });
  }

  /** One step's enabled flag + settings; the other step keeps its config. */
  saveStep(config: AudioPostProcessStepConfig): Promise<AudioPostProcessStepConfig> {
    return this.api.put('/api/settings/audio-processing/steps/{stepId}', {
      path: { stepId: config.stepId },
      body: config,
    });
  }

  /** Persists the path, then probes it, so the probe always checks what the field shows. */
  testFfmpeg(ffmpegPath: string | null): Promise<FfmpegProbeResult> {
    return this.api.post('/api/settings/audio-processing/ffmpeg/test', { body: { ffmpegPath } });
  }

  /** Newest first; only items whose Preview Source is still cached. */
  recentSamples(limit = 20): Promise<RecentAudioSampleDto[]> {
    return this.api.get('/api/audio/samples/recent', { query: { limit } });
  }

  /** Render one step with unsaved settings over a sample. 404 unknown folder, 422 evicted sample. */
  previewStep(stepId: string, request: StepPreviewRequest): Promise<StepPreviewResponse> {
    return this.api.post('/api/settings/audio-processing/steps/{stepId}/preview', {
      path: { stepId },
      body: request,
    });
  }
}
