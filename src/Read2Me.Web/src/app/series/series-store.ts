import {
  type SeriesCastDto,
  type SeriesCharacterDto,
  type SeriesDto,
  type SeriesLinkPreviewDto,
  type SeriesMemberDto,
  type SeriesMessage,
  type SeriesResolvePreviewDto,
  type SeriesShareAndLinkPreviewDto,
  SeriesApi,
} from '@app/api';
import { use } from '@app/core/services';
import { LiveService, type Unsubscribe } from '@app/live/live.service';

/**
 * One change to the series: the hub's `series` message, the same shape raised locally by this
 * store's own writes (`seriesId` null for a refused create, which names no series), or
 * `Resynced` after a hub reconnect, when a missed message may have left any series stale.
 */
export type SeriesChange =
  | SeriesMessage
  | { seriesId: string | null; kind: 'Changed' }
  | { kind: 'Resynced' };

/** A reader's re-read on a change; a returned promise holds the write that raised it. */
export type SeriesReader = (change: SeriesChange) => unknown;

/** Whether `change` asks a reader of `seriesId`'s own state (its cast board, a Voice editor) to read again. */
export function rereads(change: SeriesChange, seriesId: string | undefined): boolean {
  return change.kind === 'Resynced' || (change.kind === 'Changed' && change.seriesId === seriesId);
}

/**
 * Every Series read and write on the web (#332), and the only caller of {@link SeriesApi}. Its
 * readers (the shelf's `ProjectsStore`, the series cast board, the series Voice editor) follow
 * {@link changes}, one stream of the hub's `series` messages, this store's own writes and
 * `Resynced`. The refresh rule: each write raises its local signal whether it landed or not (a
 * multi-book write that fails partway can still have changed something) and resolves only once
 * the readers' re-reads have settled. A failed re-read is that reader's load error, never the
 * write's; the write's own error reaches the caller unchanged. The hub's echo reads a second
 * time, and each reader's sequence guard keeps an older read from overwriting a newer one.
 */
export class SeriesStore {
  private readonly api = use(SeriesApi);
  private readonly live = use(LiveService);
  private readonly readers = new Set<SeriesReader>();

  constructor() {
    this.live.on('series', (m) => void this.raise(m));
    this.live.resynced(() => void this.raise({ kind: 'Resynced' }));
  }

  /** Calls `reader` on every change until the returned function is called. */
  changes(reader: SeriesReader): Unsubscribe {
    this.readers.add(reader);
    return () => void this.readers.delete(reader);
  }

  /** Raises a local `Changed` for a write made elsewhere that changes the series (a project delete, #330). */
  changed(seriesId: string): Promise<void> {
    return this.raise({ seriesId, kind: 'Changed' });
  }

  // ---- reads ----

  /** Every series with its books (folder, position) in position order. */
  list(): Promise<SeriesDto[]> {
    return this.api.list();
  }

  /** The series cast board; throws on a failed read, which the board shows. */
  cast(id: string): Promise<SeriesCastDto> {
    return this.api.cast(id);
  }

  /**
   * The cast board for a confirm or a count: a failed read (an unreadable registry, say) answers
   * null, so the confirms name no Characters and still run, and no count shows.
   */
  async castOrNull(id: string): Promise<SeriesCastDto | null> {
    try {
      return await this.api.cast(id);
    } catch {
      return null;
    }
  }

  /** The inline series Voice editor's read. */
  character(id: string, seriesCharacterId: string): Promise<SeriesCharacterDto> {
    return this.api.character(id, seriesCharacterId);
  }

  previewLink(
    id: string,
    seriesCharacterId: string,
    folder: string,
    characterId: string,
  ): Promise<SeriesLinkPreviewDto> {
    return this.api.previewLink(id, seriesCharacterId, folder, characterId);
  }

  previewShareAndLink(
    id: string,
    members: SeriesMemberDto[],
  ): Promise<SeriesShareAndLinkPreviewDto> {
    return this.api.previewShareAndLink(id, members);
  }

  previewPush(
    id: string,
    seriesCharacterId: string,
    folder: string,
  ): Promise<SeriesResolvePreviewDto> {
    return this.api.previewPush(id, seriesCharacterId, folder);
  }

  previewTake(
    id: string,
    seriesCharacterId: string,
    folder: string,
  ): Promise<SeriesResolvePreviewDto> {
    return this.api.previewTake(id, seriesCharacterId, folder);
  }

  // ---- writes: each raises its signal and waits for the re-reads ----

  /** A new series, empty or (with `folder`) with that book as Book 1. Resolves its id. */
  async create(name: string, folder?: string): Promise<string> {
    let id: string | null = null;
    try {
      id = await this.api.create(name, folder);
      return id;
    } finally {
      await this.raise({ seriesId: id, kind: 'Changed' });
    }
  }

  async rename(id: string, name: string): Promise<void> {
    await this.write(id, () => this.api.rename(id, name));
  }

  /** Raises `Deleted` once it lands; a refused delete raises `Changed`. */
  async delete(id: string): Promise<void> {
    let deleted = false;
    try {
      await this.api.delete(id);
      deleted = true;
    } finally {
      await this.raise({ seriesId: id, kind: deleted ? 'Deleted' : 'Changed' });
    }
  }

  /** Join, or Move to with `move`; resolves the assigned Series position. */
  join(id: string, folder: string, move = false): Promise<number> {
    return this.write(id, () => this.api.join(id, folder, move));
  }

  leave(id: string, folder: string): Promise<void> {
    return this.write(id, () => this.api.leave(id, folder));
  }

  async savePositions(id: string, positions: Record<string, number>): Promise<void> {
    await this.write(id, () => this.api.savePositions(id, positions));
  }

  async uploadPoster(id: string, file: File): Promise<void> {
    await this.write(id, () => this.api.uploadPoster(id, file));
  }

  clearPoster(id: string): Promise<void> {
    return this.write(id, () => this.api.clearPoster(id));
  }

  /** Share as new; resolves the new Series character's id. */
  share(id: string, folder: string, characterId: string): Promise<string> {
    return this.write(id, () => this.api.share(id, folder, characterId));
  }

  link(id: string, seriesCharacterId: string, folder: string, characterId: string): Promise<void> {
    return this.write(id, () => this.api.link(id, seriesCharacterId, folder, characterId));
  }

  unlink(id: string, seriesCharacterId: string, folder: string): Promise<void> {
    return this.write(id, () => this.api.unlink(id, seriesCharacterId, folder));
  }

  shareAndLink(id: string, members: SeriesMemberDto[]): Promise<string> {
    return this.write(id, () => this.api.shareAndLink(id, members));
  }

  notTheSame(
    id: string,
    seriesCharacterId: string,
    folder: string,
    characterId: string,
  ): Promise<void> {
    return this.write(id, () => this.api.notTheSame(id, seriesCharacterId, folder, characterId));
  }

  notTheSameRow(id: string, members: SeriesMemberDto[]): Promise<void> {
    return this.write(id, () => this.api.notTheSameRow(id, members));
  }

  /** Push `folder`'s Voices of the Series character into every other linked book. */
  push(id: string, seriesCharacterId: string, folder: string): Promise<void> {
    return this.write(id, () => this.api.push(id, seriesCharacterId, folder));
  }

  /** Take the series copy into `folder`. */
  take(id: string, seriesCharacterId: string, folder: string): Promise<void> {
    return this.write(id, () => this.api.take(id, seriesCharacterId, folder));
  }

  /** Sends one write, then raises `Changed` for its series whatever it answered. */
  private async write<T>(id: string, send: () => Promise<T>): Promise<T> {
    try {
      return await send();
    } finally {
      await this.changed(id);
    }
  }

  /** Hands `change` to every reader; resolves once each re-read settles, failed or not. */
  private async raise(change: SeriesChange): Promise<void> {
    await Promise.allSettled(
      Array.from(this.readers, async (reader) => {
        await reader(change);
      }),
    );
  }
}
