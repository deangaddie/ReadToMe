import { beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../testing/fake-api';
import { FakeLive } from '../../testing/fake-live';
import { type SeriesChange, SeriesStore } from './series-store';

let api: FakeApi;
let live: FakeLive;
let store: SeriesStore;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  store = new SeriesStore();
});

describe('SeriesStore', () => {
  it('a write raises a local Changed for its series once it lands', async () => {
    api.on('DELETE', '/api/series/h/books/four', undefined);
    const seen: SeriesChange[] = [];
    store.changes((c) => void seen.push(c));

    await store.leave('h', 'four');

    expect(seen).toEqual([{ seriesId: 'h', kind: 'Changed' }]);
  });

  it('a failed write still raises the signal, and the write’s own error reaches the caller', async () => {
    api.on('PUT', '/api/series/h/books/four', () => problem(409, 'in another series'));
    const seen: SeriesChange[] = [];
    store.changes((c) => void seen.push(c));

    const error = await store.join('h', 'four').catch((e: Error) => e);

    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toContain('in another series');
    expect(seen).toEqual([{ seriesId: 'h', kind: 'Changed' }]);
  });

  it('a write resolves only after the readers’ re-reads have settled', async () => {
    api.on('DELETE', '/api/series/h/poster', undefined);
    let reread!: () => void;
    let settled = false;
    store.changes(() => new Promise<void>((r) => (reread = r)));

    const write = store.clearPoster('h').then(() => (settled = true));
    await new Promise((r) => setTimeout(r, 0));
    expect(settled).toBe(false);
    reread();
    await write;

    expect(settled).toBe(true);
  });

  it('a failed re-read doesn’t fail the write', async () => {
    api.on('PATCH', '/api/series/h', { id: 'h', name: 'Oz', poster: {}, books: [] });
    store.changes(() => Promise.reject(new Error('list unreadable')));
    store.changes(() => {
      throw new Error('board unreadable');
    });

    await store.rename('h', 'Oz');
  });

  it('a create names the new series; a refused one names none', async () => {
    api.on('POST', '/api/series', { id: 'n' });
    const seen: SeriesChange[] = [];
    store.changes((c) => void seen.push(c));

    expect(await store.create('Austen')).toBe('n');
    api.on('POST', '/api/series', () => problem(409, 'taken'));
    await store.create('Austen').catch(() => undefined);

    expect(seen).toEqual([
      { seriesId: 'n', kind: 'Changed' },
      { seriesId: null, kind: 'Changed' },
    ]);
  });

  it('a delete raises Deleted once it lands, Changed when refused', async () => {
    const seen: SeriesChange[] = [];
    store.changes((c) => void seen.push(c));

    api.on('DELETE', '/api/series/h', undefined);
    await store.delete('h');
    api.on('DELETE', '/api/series/h', () => problem(422, 'unreadable'));
    await store.delete('h').catch(() => undefined);

    expect(seen).toEqual([
      { seriesId: 'h', kind: 'Deleted' },
      { seriesId: 'h', kind: 'Changed' },
    ]);
  });

  it('carries the hub’s series messages and Resynced after a reconnect', () => {
    const seen: SeriesChange[] = [];
    const stop = store.changes((c) => void seen.push(c));

    live.emit('series', { seriesId: 'h', kind: 'Changed' });
    live.emit('series', { seriesId: 'x', kind: 'Deleted' });
    live.resync();
    stop();
    live.emit('series', { seriesId: 'h', kind: 'Changed' });

    expect(seen).toEqual([
      { seriesId: 'h', kind: 'Changed' },
      { seriesId: 'x', kind: 'Deleted' },
      { kind: 'Resynced' },
    ]);
  });

  it('changed() raises a local Changed for a write made elsewhere (a project delete)', async () => {
    const seen: SeriesChange[] = [];
    store.changes((c) => void seen.push(c));

    await store.changed('h');

    expect(seen).toEqual([{ seriesId: 'h', kind: 'Changed' }]);
  });

  it('castOrNull answers null when the cast can’t be read', async () => {
    api.on('GET', '/api/series/h/cast', () => problem(422, 'registry unreadable'));

    expect(await store.castOrNull('h')).toBeNull();
    expect(await store.cast('h').catch(() => 'threw')).toBe('threw');
  });
});
