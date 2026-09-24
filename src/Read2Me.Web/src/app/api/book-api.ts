import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { BookCommand } from './book-commands';
import type {
  AudioItemRefDto,
  BookOverviewDto,
  BulkAssignPreviewDto,
  BulkAssignPreviewRequest,
  ChapterVoicesDto,
  CharacterDto,
  CommandResponse,
  Guid,
  NodeChildrenDto,
  NodeLevel,
  ParagraphRefDto,
} from './dtos';
import { projectUrl } from './projects-api';

/**
 * `BookEndpoints.cs` + `CommandEndpoints.cs`: the read model and the one mutation channel. Per spec
 * D6 the client never patches entities; it posts a {@link BookCommand} and refreshes from the
 * receipt the live hub echoes (ticket 07). 400 = unknown command type, 422 = the command was
 * rejected (the verbatim reason is in `ApiError.detail`).
 */
@Injectable({ providedIn: 'root' })
export class BookApi {
  private readonly api = inject(ApiClient);

  overview(folder: string): Promise<BookOverviewDto> {
    return this.api.get<BookOverviewDto>(`${projectUrl(folder)}/book`);
  }

  /** volume → parts, part → chapters, chapter → paragraphs with their items. */
  children(folder: string, level: NodeLevel, id: Guid): Promise<NodeChildrenDto> {
    return this.api.get<NodeChildrenDto>(`${projectUrl(folder)}/nodes/${level}/${id}/children`);
  }

  /** The voice each speech item of a chapter resolves to, keyed by item id. */
  chapterVoices(folder: string, chapterId: Guid): Promise<ChapterVoicesDto> {
    return this.api.get<ChapterVoicesDto>(
      `${projectUrl(folder)}/nodes/chapter/${chapterId}/voices`,
    );
  }

  /**
   * The Character paragraphs under a node with their ancestry (ticket 12): what a tree checkbox
   * selects; `unprocessedOnly` keeps those still holding an unattributed line.
   */
  paragraphIds(
    folder: string,
    level: NodeLevel,
    id: Guid,
    unprocessedOnly = false,
  ): Promise<ParagraphRefDto[]> {
    return this.api.get<ParagraphRefDto[]>(
      `${projectUrl(folder)}/nodes/${level}/${id}/paragraph-ids`,
      unprocessedOnly ? { unprocessedOnly: true } : undefined,
    );
  }

  /**
   * The speech items under a node that have a speaker to read them, with their ancestry (ticket
   * 13): what an audio tree checkbox selects; `needsAudioOnly` keeps those still missing a WAV;
   * `narratorOnlyMode` counts unattributed lines as readable.
   */
  itemIds(
    folder: string,
    level: NodeLevel,
    id: Guid,
    options: { needsAudioOnly?: boolean; narratorOnlyMode?: boolean } = {},
  ): Promise<AudioItemRefDto[]> {
    const params: Record<string, boolean> = {};
    if (options.needsAudioOnly) params["needsAudioOnly"] = true;
    if (options.narratorOnlyMode) params["narratorOnlyMode"] = true;
    return this.api.get<AudioItemRefDto[]>(
      `${projectUrl(folder)}/nodes/${level}/${id}/item-ids`,
      Object.keys(params).length > 0 ? params : undefined,
    );
  }

  characters(folder: string): Promise<CharacterDto[]> {
    return this.api.get<CharacterDto[]>(`${projectUrl(folder)}/characters`);
  }

  /** What a bulk speaker assign over the paragraphs would write (the confirm quotes it). */
  bulkAssignPreview(folder: string, paragraphIds: Guid[]): Promise<BulkAssignPreviewDto> {
    const request: BulkAssignPreviewRequest = { paragraphIds };
    return this.api.post<BulkAssignPreviewDto>(
      `${projectUrl(folder)}/characters/bulk-assign-preview`,
      request,
    );
  }

  execute(folder: string, command: BookCommand): Promise<CommandResponse> {
    return this.api.post<CommandResponse>(`${projectUrl(folder)}/commands`, command);
  }
}
