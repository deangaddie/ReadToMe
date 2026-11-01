import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  Guid,
  PronunciationDeletedDto,
  PronunciationDraftRequest,
  PronunciationDto,
  PronunciationImpactDto,
  PronunciationSavedDto,
  PronunciationSpanReport,
  SetPronunciationRequest,
  TryPronunciationRequest,
} from './dtos';
import { projectUrl } from './projects-api';

/**
 * `PronunciationEndpoints.cs`: a book's Pronunciation list. Each write is one History step and
 * discards the audio of the items whose Spoken text it changes; 409 while audio generation is
 * queued or running for the project, or another entry has the written form.
 */
export class PronunciationsApi {
  private readonly api = use(ApiClient);

  /** The list sorted by written form, each entry with its reach. Read at the viewed step. */
  list(folder: string): Promise<PronunciationDto[]> {
    return this.api.untypedGet<PronunciationDto[]>(`${projectUrl(folder)}/pronunciations`);
  }

  /** Where the list matches in these chapters (the Book view's marks). Read at the viewed step. */
  matches(folder: string, chapterIds: readonly Guid[]): Promise<PronunciationSpanReport> {
    return this.api.untypedGet<PronunciationSpanReport>(
      `${projectUrl(folder)}/pronunciations/matches`,
      {
        chapterIds,
      },
    );
  }

  add(folder: string, body: SetPronunciationRequest): Promise<PronunciationSavedDto> {
    return this.api.untypedPost<PronunciationSavedDto>(
      `${projectUrl(folder)}/pronunciations`,
      body,
    );
  }

  /** Saving an unchanged entry writes nothing and lands no step. */
  edit(folder: string, id: Guid, body: SetPronunciationRequest): Promise<PronunciationSavedDto> {
    return this.api.untypedPut<PronunciationSavedDto>(
      `${projectUrl(folder)}/pronunciations/${id}`,
      body,
    );
  }

  remove(folder: string, id: Guid): Promise<PronunciationDeletedDto> {
    return this.api.untypedDelete<PronunciationDeletedDto>(
      `${projectUrl(folder)}/pronunciations/${id}`,
    );
  }

  /** A dry run of a draft: its reach, the audio a save would discard, overlaps and the duplicate. */
  impact(folder: string, draft: PronunciationDraftRequest): Promise<PronunciationImpactDto> {
    return this.api.untypedPost<PronunciationImpactDto>(
      `${projectUrl(folder)}/pronunciations/impact`,
      draft,
    );
  }

  /**
   * Renders a try-out sentence and answers its WAV; nothing is stored. Aborting `signal` drops the
   * request. 409 while attribution, audio generation or a voice batch has the GPU.
   */
  tryOut(folder: string, request: TryPronunciationRequest, signal?: AbortSignal): Promise<Blob> {
    return this.api.untypedPostForBlob(`${projectUrl(folder)}/pronunciations/try`, request, signal);
  }
}
