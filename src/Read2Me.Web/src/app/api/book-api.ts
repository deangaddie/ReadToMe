import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { BookCommand } from './book-commands';
import type {
  BookOverviewDto,
  CharacterDto,
  CommandResponse,
  Guid,
  NodeChildrenDto,
  NodeLevel,
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

  characters(folder: string): Promise<CharacterDto[]> {
    return this.api.get<CharacterDto[]>(`${projectUrl(folder)}/characters`);
  }

  execute(folder: string, command: BookCommand): Promise<CommandResponse> {
    return this.api.post<CommandResponse>(`${projectUrl(folder)}/commands`, command);
  }
}
