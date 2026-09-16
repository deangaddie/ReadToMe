import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { CharacterLineDto, CharacterSummaryDto, Guid, ParagraphContextDto } from './dtos';
import { projectUrl } from './projects-api';

/** The widest context window the host honours on either side (`CharacterEndpoints.MaxContextWindow`). */
export const MAX_CONTEXT_WINDOW = 10;

/** `CharacterEndpoints.cs` (ticket 15): the cast page's reads. Writes go through `BookApi.execute`. */
@Injectable({ providedIn: 'root' })
export class CharactersApi {
  private readonly api = inject(ApiClient);

  /** Every character as a roster row, narrator first then by name. */
  summary(folder: string): Promise<CharacterSummaryDto[]> {
    return this.api.get<CharacterSummaryDto[]>(`${projectUrl(folder)}/characters/summary`);
  }

  /** The items a character speaks, in book order. Empty for an unknown character. */
  lines(folder: string, characterId: Guid): Promise<CharacterLineDto[]> {
    return this.api.get<CharacterLineDto[]>(
      `${projectUrl(folder)}/characters/${characterId}/lines`,
    );
  }

  /** A paragraph with up to `before`/`after` neighbours (each 0..10) of the same chapter. 404 when not in the chapter. */
  context(
    folder: string,
    chapterId: Guid,
    paragraphId: Guid,
    before: number,
    after: number,
  ): Promise<ParagraphContextDto> {
    return this.api.get<ParagraphContextDto>(
      `${projectUrl(folder)}/paragraphs/${paragraphId}/context`,
      { chapterId, before, after },
    );
  }
}
