import { afterEach, describe, expect, it } from 'bun:test';
import { resetServices, use } from '@app/core/services';
import { ApiClient, type FetchFn } from './api-client';
import { StepView, readsAtStep } from './step-view';
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
    view.view('dune', 's8');
    expect(view.at('dune')).toBe('s8');
    expect(view.at('emma')).toBeNull();
    view.clear('emma');
    expect(view.at('dune')).toBe('s8');
    view.clear('dune');
    expect(view.at('dune')).toBeNull();
  });

  it('adds ?at= to every Project state read of the viewed project, beside its own query', async () => {
    use(StepView).view('dune', 's8');
    const { api, urls } = client();

    await api.get('/api/projects/dune/book');
    await api.get('/api/projects/dune');
    await api.get('/api/projects/dune/characters/c1/voice-rules/preview', { chapterId: 'ch' });

    expect(urls).toEqual([
      '/api/projects/dune/book?at=s8',
      '/api/projects/dune?at=s8',
      '/api/projects/dune/characters/c1/voice-rules/preview?chapterId=ch&at=s8',
    ]);
  });

  it('leaves live reads, writes, other projects and the current project alone', async () => {
    const { api, urls } = client();
    await api.get('/api/projects/dune/book');
    use(StepView).view('dune', 's8');

    await api.get('/api/projects/dune/history');
    await api.get('/api/projects/dune/audio/reviews');
    await api.get('/api/projects/emma/book');
    await api.get('/api/projects');
    await api.patch('/api/projects/dune', {});

    expect(urls).toEqual([
      '/api/projects/dune/book',
      '/api/projects/dune/history',
      '/api/projects/dune/audio/reviews',
      '/api/projects/emma/book',
      '/api/projects',
      '/api/projects/dune',
    ]);
  });

  it('matches a folder name that needed encoding', () => {
    use(StepView).view('my book', 's1');
    expect(readsAtStep('/api/projects/my%20book/characters')).toBe('s1');
    expect(readsAtStep('/api/projects/my%20book/nodes/chapter/c1/children')).toBe('s1');
    expect(readsAtStep('/api/projects/my%20book/nodes/chapter/c1/children/extra')).toBeNull();
  });

  it('reads state files at the viewed step; item takes, which are not Project state, never', () => {
    use(StepView).view('dune', 's8');
    expect(stateFileUrl('dune', 'voices/paul.wav', 3)).toBe(
      '/workspace/dune/voices/paul.wav?v=3&at=s8',
    );
    expect(stateFileUrl('dune', 'cover.jpg')).toBe('/workspace/dune/cover.jpg?at=s8');
    expect(voiceOriginalUrl('dune', 'v1')).toBe('/api/projects/dune/voices/v1/original.wav?at=s8');
    expect(workspaceUrl('dune', 'audio/i1.wav')).toBe('/workspace/dune/audio/i1.wav');
    expect(stateFileUrl('emma', 'cover.jpg')).toBe('/workspace/emma/cover.jpg');
  });
});
