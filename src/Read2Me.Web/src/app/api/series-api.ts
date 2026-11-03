import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { SeriesCastDto, SeriesDto, SeriesLinkPreviewDto } from './dtos';

/**
 * `SeriesEndpoints.cs`: the series registry and its lifecycle (Series lifecycle, #206). Books are
 * addressed by folder. Registry writes make no History step; each sends the `series` hub signal.
 */
export class SeriesApi {
  private readonly api = use(ApiClient);

  /** Every series with its books (folder, position) in position order. */
  list(): Promise<SeriesDto[]> {
    return this.api.get('/api/series');
  }

  /** 201 `{ id }`; with `folder`, that book joins as Book 1. 409 name taken or the book is in another series. */
  async create(name: string, folder?: string): Promise<string> {
    const { id } = await this.api.post('/api/series', { body: { name, folder: folder ?? null } });
    return id;
  }

  /** 409 when the name is taken, ignoring case. Answers the series. */
  rename(id: string, name: string): Promise<SeriesDto> {
    return this.api.patch('/api/series/{id}', { path: { id }, body: { name } });
  }

  /** Every book leaves and keeps its Voices; the registry folder goes. */
  delete(id: string): Promise<void> {
    return this.api.delete('/api/series/{id}', { path: { id } });
  }

  /** Join (or, with `move`, Move to); answers the book's Series position. */
  async join(id: string, folder: string, move = false): Promise<number> {
    const { position } = await this.api.put('/api/series/{id}/books/{folder}', {
      path: { id, folder },
      body: { move },
    });
    return position;
  }

  /** The book leaves; nobody is renumbered. */
  leave(id: string, folder: string): Promise<void> {
    return this.api.delete('/api/series/{id}/books/{folder}', { path: { id, folder } });
  }

  /** Saves the whole position set; 409 on a tie or a stale set. Answers the series. */
  savePositions(id: string, positions: Record<string, number>): Promise<SeriesDto> {
    return this.api.put('/api/series/{id}/positions', { path: { id }, body: { positions } });
  }

  /**
   * Upload or replace the poster: multipart `file` under the cover's rules (`COVER_ACCEPT`,
   * `COVER_MAX_BYTES`); 400 otherwise. Answers the series.
   */
  uploadPoster(id: string, file: File): Promise<SeriesDto> {
    const form = new FormData();
    form.set('file', file, file.name);
    return this.api.putForm('/api/series/{id}/poster', { path: { id } }, form);
  }

  /** Removes the uploaded poster (204, also when there is none); the fallback cover shows again. */
  clearPoster(id: string): Promise<void> {
    return this.api.delete('/api/series/{id}/poster', { path: { id } });
  }

  // ---- Series characters and links (#220) ----

  /** The series cast board: books in position order, one row per Series character, each book's unlinked Characters. */
  cast(id: string): Promise<SeriesCastDto> {
    return this.api.get('/api/series/{id}/cast', { path: { id } });
  }

  /** Share as new: a Series character made from this Character. Resolves its id; 409 when already linked. */
  async share(id: string, folder: string, characterId: string): Promise<string> {
    const { seriesCharacterId } = await this.api.post('/api/series/{id}/characters', {
      path: { id },
      body: { folder, characterId },
    });
    return seriesCharacterId;
  }

  /** Links the book's Character, running the Voice merge in each affected book. */
  link(id: string, seriesCharacterId: string, folder: string, characterId: string): Promise<void> {
    return this.api.put('/api/series/{id}/characters/{seriesCharacterId}/links/{folder}', {
      path: { id, seriesCharacterId, folder },
      body: { characterId },
    });
  }

  /** Unlinks the book's Character; the book keeps its copy of the Voices. */
  unlink(id: string, seriesCharacterId: string, folder: string): Promise<void> {
    return this.api.delete('/api/series/{id}/characters/{seriesCharacterId}/links/{folder}', {
      path: { id, seriesCharacterId, folder },
    });
  }

  /** What linking would copy, for the Link confirm. */
  previewLink(
    id: string,
    seriesCharacterId: string,
    folder: string,
    characterId: string,
  ): Promise<SeriesLinkPreviewDto> {
    return this.api.get('/api/series/{id}/characters/{seriesCharacterId}/links/{folder}/preview', {
      path: { id, seriesCharacterId, folder },
      query: { characterId },
    });
  }
}
