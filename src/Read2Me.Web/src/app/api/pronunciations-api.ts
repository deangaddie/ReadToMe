import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  Guid,
  PronunciationDeleted,
  PronunciationDraftRequest,
  PronunciationEntry,
  PronunciationImpactReport,
  PronunciationSaved,
  PronunciationSpanReport,
  SetPronunciationRequest,
  TryPronunciationRequest,
} from './dtos';

/**
 * `PronunciationEndpoints.cs`: a book's Pronunciation list. Each write is one History step and
 * discards the audio of the items whose Spoken text it changes; 409 while audio generation is
 * queued or running for the project, or another entry has the written form.
 */
export class PronunciationsApi {
  private readonly api = use(ApiClient);

  /** The list sorted by written form, each entry with its reach. Read at the viewed step. */
  list(folder: string): Promise<PronunciationEntry[]> {
    return this.api.get('/api/projects/{folder}/pronunciations', { path: { folder } });
  }

  /** Where the list matches in these chapters (the Book view's marks). Read at the viewed step. */
  matches(folder: string, chapterIds: readonly Guid[]): Promise<PronunciationSpanReport> {
    return this.api.get('/api/projects/{folder}/pronunciations/matches', {
      path: { folder },
      query: { chapterIds: [...chapterIds] },
    });
  }

  add(folder: string, body: SetPronunciationRequest): Promise<PronunciationSaved> {
    return this.api.post('/api/projects/{folder}/pronunciations', { path: { folder }, body });
  }

  /** Saving an unchanged entry writes nothing and lands no step. */
  edit(folder: string, id: Guid, body: SetPronunciationRequest): Promise<PronunciationSaved> {
    return this.api.put('/api/projects/{folder}/pronunciations/{id}', {
      path: { folder, id },
      body,
    });
  }

  remove(folder: string, id: Guid): Promise<PronunciationDeleted> {
    return this.api.delete('/api/projects/{folder}/pronunciations/{id}', { path: { folder, id } });
  }

  /** A dry run of a draft: its reach, the audio a save would discard, overlaps and the duplicate. */
  impact(folder: string, draft: PronunciationDraftRequest): Promise<PronunciationImpactReport> {
    return this.api.post('/api/projects/{folder}/pronunciations/impact', {
      path: { folder },
      body: draft,
    });
  }

  /**
   * Renders a try-out sentence and answers its WAV; nothing is stored. Aborting `signal` drops the
   * request. 409 while attribution, audio generation or a voice batch has the GPU.
   */
  tryOut(folder: string, request: TryPronunciationRequest, signal?: AbortSignal): Promise<Blob> {
    return this.api.postForBlob(
      '/api/projects/{folder}/pronunciations/try',
      { path: { folder }, body: request },
      signal,
    );
  }
}
