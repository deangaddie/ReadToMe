import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { SeriesDto } from './dtos';

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
}
