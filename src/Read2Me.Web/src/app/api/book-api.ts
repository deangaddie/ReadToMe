import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { BookCommand } from './book-commands';
import type {
  AudioItemRefDto,
  BookOverviewDto,
  BulkAssignPreviewDto,
  ChapterVoicesDto,
  CharacterDto,
  CommandResponse,
  Guid,
  NarrationReleasePreviewDto,
  NarrationReleaseScope,
  NodeChildrenDto,
  NodeLevel,
  ParagraphRefDto,
} from './dtos';

/**
 * `BookEndpoints.cs` + `CommandEndpoints.cs`: the read model and the one mutation channel. Per spec
 * D6 the client never patches entities; it posts a {@link BookCommand} and refreshes from the
 * receipt the live hub echoes (ticket 07). 400 = unknown command type, 422 = the command was
 * rejected (the verbatim reason is in `ApiError.detail`).
 */
export class BookApi {
  private readonly api = use(ApiClient);

  overview(folder: string): Promise<BookOverviewDto> {
    return this.api.get('/api/projects/{folder}/book', { path: { folder } });
  }

  /** volume → parts, part → chapters, chapter → paragraphs with their items. */
  children(folder: string, level: NodeLevel, id: Guid): Promise<NodeChildrenDto> {
    return this.api.get('/api/projects/{folder}/nodes/{level}/{id}/children', {
      path: { folder, level, id },
    });
  }

  /** The voice each speech item of a chapter resolves to, keyed by item id. */
  chapterVoices(folder: string, chapterId: Guid): Promise<ChapterVoicesDto> {
    return this.api.get('/api/projects/{folder}/nodes/Chapter/{id}/voices', {
      path: { folder, id: chapterId },
    });
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
    return this.api.get('/api/projects/{folder}/nodes/{level}/{id}/paragraph-ids', {
      path: { folder, level, id },
      query: unprocessedOnly ? { unprocessedOnly: true } : {},
    });
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
    return this.api.get('/api/projects/{folder}/nodes/{level}/{id}/item-ids', {
      path: { folder, level, id },
      query: {
        ...(options.needsAudioOnly ? { needsAudioOnly: true } : {}),
        ...(options.narratorOnlyMode ? { narratorOnlyMode: true } : {}),
      },
    });
  }

  characters(folder: string): Promise<CharacterDto[]> {
    return this.api.get('/api/projects/{folder}/characters', { path: { folder } });
  }

  /** What a bulk speaker assign over the paragraphs would write (the confirm quotes it). */
  bulkAssignPreview(folder: string, paragraphIds: Guid[]): Promise<BulkAssignPreviewDto> {
    return this.api.post('/api/projects/{folder}/characters/bulk-assign-preview', {
      path: { folder },
      body: { paragraphIds },
    });
  }

  /** What a ReleaseNarration over the node would write (the confirm quotes it). */
  releaseNarrationPreview(
    folder: string,
    scope: NarrationReleaseScope,
    nodeId: Guid,
  ): Promise<NarrationReleasePreviewDto> {
    return this.api.post('/api/projects/{folder}/narration/release-preview', {
      path: { folder },
      body: { scope, nodeId },
    });
  }

  /**
   * The host binds the body as a raw `JsonObject`, so the document types it as an open object; the
   * {@link BookCommand} union stays hand-written. Spreading it makes an object type that fits one.
   */
  execute(folder: string, command: BookCommand): Promise<CommandResponse> {
    return this.api.post('/api/projects/{folder}/commands', {
      path: { folder },
      body: { ...command },
    });
  }
}
