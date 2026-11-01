import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  ChapterVoicePreviewDto,
  CharacterLineDto,
  CharacterSummaryDto,
  Guid,
  ParagraphContextDto,
  VoiceRuleDto,
} from './dtos';

/** The widest context window the host honours on either side (`CharacterEndpoints.MaxContextWindow`). */
export const MAX_CONTEXT_WINDOW = 10;

/** `CharacterEndpoints.cs` (tickets 15, 17): the cast page's reads. Writes go through `BookApi.execute`. */
export class CharactersApi {
  private readonly api = use(ApiClient);

  /** Every character as a roster row, narrator first then by name. */
  summary(folder: string): Promise<CharacterSummaryDto[]> {
    return this.api.get('/api/projects/{folder}/characters/summary', { path: { folder } });
  }

  /** The items a character speaks, in book order. Empty for an unknown character. */
  lines(folder: string, characterId: Guid): Promise<CharacterLineDto[]> {
    return this.api.get('/api/projects/{folder}/characters/{id}/lines', {
      path: { folder, id: characterId },
    });
  }

  /** A character's voice rules in evaluation order (default first). Empty for an unknown character. */
  voiceRules(folder: string, characterId: Guid): Promise<VoiceRuleDto[]> {
    return this.api.get('/api/projects/{folder}/characters/{id}/voice-rules', {
      path: { folder, id: characterId },
    });
  }

  /** The voice the rules pick at the start of every chapter, in book order. */
  voiceRulePreview(folder: string, characterId: Guid): Promise<ChapterVoicePreviewDto[]> {
    return this.api.get('/api/projects/{folder}/characters/{id}/voice-rules/preview', {
      path: { folder, id: characterId },
    });
  }

  /** A paragraph with up to `before`/`after` neighbours (each 0..10) of the same chapter. 404 when not in the chapter. */
  context(
    folder: string,
    chapterId: Guid,
    paragraphId: Guid,
    before: number,
    after: number,
  ): Promise<ParagraphContextDto> {
    return this.api.get('/api/projects/{folder}/paragraphs/{paragraphId}/context', {
      path: { folder, paragraphId },
      query: { chapterId, before, after },
    });
  }
}
