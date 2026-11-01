import { afterEach, describe, expect, it } from 'bun:test';
import { resetServices, use } from '@app/core/services';
import { ApiClient, type FetchFn } from './api-client';
import {
  STALE_READ_ONLY,
  StepView,
  VIEWING_READ_ONLY,
  blockedReason,
  readsAtStep,
  withLocal,
} from './step-view';
import { voiceOriginalUrl } from './voice-editor-api';
import { stateFileUrl, workspaceUrl } from './workspace-url';

function client() {
  const urls: string[] = [];
  const fetchFn: FetchFn = async (url) => {
    urls.push(url);
    return Response.json({});
  };
  return { api: new ApiClient(fetchFn), urls };
}

describe('StepView', () => {
  afterEach(() => resetServices());

  it('names the step a folder is viewed at, and nothing for another folder or once cleared', () => {
    const view = use(StepView);
    view.view('dracula', 's8');
    expect(view.at('dracula')).toBe('s8');
    expect(view.at('emma')).toBeNull();
    view.clear('emma');
    expect(view.at('dracula')).toBe('s8');
    view.clear('dracula');
    expect(view.at('dracula')).toBeNull();
  });

  it('adds ?at= to every Project state read of the viewed project, beside its own query', async () => {
    use(StepView).view('dracula', 's8');
    const { api, urls } = client();

    const path = { folder: 'dracula' };
    await api.get('/api/projects/{folder}/book', { path });
    await api.get('/api/projects/{folder}', { path });
    await api.get('/api/projects/{folder}/pronunciations/matches', {
      path,
      query: { chapterIds: ['ch'] },
    });

    expect(urls).toEqual([
      '/api/projects/dracula/book?at=s8',
      '/api/projects/dracula?at=s8',
      '/api/projects/dracula/pronunciations/matches?chapterIds=ch&at=s8',
    ]);
  });

  it('leaves live reads, writes, other projects and the current project alone', async () => {
    const { api, urls } = client();
    const dracula = { folder: 'dracula' };
    await api.get('/api/projects/{folder}/book', { path: dracula });
    use(StepView).view('dracula', 's8');

    await api.get('/api/projects/{folder}/history', { path: dracula });
    await api.get('/api/projects/{folder}/audio/reviews', { path: dracula });
    await api.get('/api/projects/{folder}/book', { path: { folder: 'emma' } });
    await api.get('/api/projects');
    await api.patch('/api/projects/{folder}', { path: dracula, body: {} });

    expect(urls).toEqual([
      '/api/projects/dracula/book',
      '/api/projects/dracula/history',
      '/api/projects/dracula/audio/reviews',
      '/api/projects/emma/book',
      '/api/projects',
      '/api/projects/dracula',
    ]);
  });

  it('matches a folder name that needed encoding', () => {
    use(StepView).view('my book', 's1');
    expect(readsAtStep('/api/projects/my%20book/characters')).toBe('s1');
    expect(readsAtStep('/api/projects/my%20book/nodes/Chapter/c1/children')).toBe('s1');
    expect(readsAtStep('/api/projects/my%20book/nodes/Chapter/c1/children/extra')).toBeNull();
  });

  it('reads state files at the viewed step; item takes, which are not Project state, never', () => {
    use(StepView).view('dracula', 's8');
    expect(stateFileUrl('dracula', 'voices/arthur.wav', 3)).toBe(
      '/workspace/dracula/voices/arthur.wav?v=3&at=s8',
    );
    expect(stateFileUrl('dracula', 'cover.jpg')).toBe('/workspace/dracula/cover.jpg?at=s8');
    expect(voiceOriginalUrl('dracula', 'v1')).toBe(
      '/api/projects/dracula/voices/v1/original.wav?at=s8',
    );
    expect(workspaceUrl('dracula', 'audio/i1.wav')).toBe('/workspace/dracula/audio/i1.wav');
    expect(stateFileUrl('emma', 'cover.jpg')).toBe('/workspace/emma/cover.jpg');
  });
});

describe('blockedReason', () => {
  const open = { viewing: false, stale: false, saving: false };

  it('is open when nothing blocks and there is no local busy', () => {
    expect(blockedReason(open)).toBeNull();
    expect(blockedReason(open, false)).toBeNull();
  });

  it('names each blocked reason on its own', () => {
    expect(blockedReason({ ...open, viewing: true })).toEqual({ reason: VIEWING_READ_ONLY });
    expect(blockedReason({ ...open, stale: true })).toEqual({ reason: STALE_READ_ONLY });
    expect(blockedReason(open, 'Bulk assign waits')).toEqual({ reason: 'Bulk assign waits' });
  });

  it('blocks without a reason while saving, or for a plain local flag', () => {
    expect(blockedReason({ ...open, saving: true })).toEqual({ reason: null });
    expect(blockedReason(open, true)).toEqual({ reason: null });
  });

  it('gives one reason, in the order viewing > stale > local reason > none', () => {
    const all = { viewing: true, stale: true, saving: true };
    expect(blockedReason(all, 'local')).toEqual({ reason: VIEWING_READ_ONLY });
    expect(blockedReason({ ...all, viewing: false }, 'local')).toEqual({
      reason: STALE_READ_ONLY,
    });
    expect(blockedReason({ ...open, saving: true }, 'local')).toEqual({ reason: 'local' });
    expect(blockedReason({ ...open, saving: true }, true)).toEqual({ reason: null });
  });
});

describe('withLocal', () => {
  it('folds a local busy into a block, its reason only filling a block without one', () => {
    expect(withLocal(null)).toBeNull();
    expect(withLocal(null, false)).toBeNull();
    expect(withLocal(null, true)).toEqual({ reason: null });
    expect(withLocal(null, 'local')).toEqual({ reason: 'local' });
    expect(withLocal(null, '')).toEqual({ reason: null });
    expect(withLocal({ reason: null }, 'local')).toEqual({ reason: 'local' });
    expect(withLocal({ reason: STALE_READ_ONLY }, 'local')).toEqual({ reason: STALE_READ_ONLY });
    expect(withLocal({ reason: STALE_READ_ONLY }, false)).toEqual({ reason: STALE_READ_ONLY });
  });
});
