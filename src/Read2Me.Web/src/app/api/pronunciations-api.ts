import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  Guid,
  PronunciationDeletedDto,
  PronunciationDraftRequest,
  PronunciationDto,
  PronunciationImpactDto,
  PronunciationSavedDto,
  SetPronunciationRequest,
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
    return this.api.get<PronunciationDto[]>(`${projectUrl(folder)}/pronunciations`);
  }

  add(folder: string, body: SetPronunciationRequest): Promise<PronunciationSavedDto> {
    return this.api.post<PronunciationSavedDto>(`${projectUrl(folder)}/pronunciations`, body);
  }

  /** Saving an unchanged entry writes nothing and lands no step. */
  edit(folder: string, id: Guid, body: SetPronunciationRequest): Promise<PronunciationSavedDto> {
    return this.api.put<PronunciationSavedDto>(`${projectUrl(folder)}/pronunciations/${id}`, body);
  }

  remove(folder: string, id: Guid): Promise<PronunciationDeletedDto> {
    return this.api.delete<PronunciationDeletedDto>(`${projectUrl(folder)}/pronunciations/${id}`);
  }

  /** A dry run of a draft: its reach, the audio a save would discard, overlaps and the duplicate. */
  impact(folder: string, draft: PronunciationDraftRequest): Promise<PronunciationImpactDto> {
    return this.api.post<PronunciationImpactDto>(
      `${projectUrl(folder)}/pronunciations/impact`,
      draft,
    );
  }
}
