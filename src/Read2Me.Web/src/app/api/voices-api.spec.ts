import { beforeEach, describe, expect, it } from 'bun:test';
import { resetServices } from '@app/core/services';
import { FakeApi, problem } from '../../testing/fake-api';
import { ApiError } from './api-error';
import { VoicesApi } from './voices-api';

/** The reference-audio upload: the browser's multipart body the host's `[FromForm] file` binds. */
let api: FakeApi;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
});

const AUDIO_URL = '/api/projects/my%20book/voices/v1/audio';

describe('VoicesApi.uploadAudio', () => {
  it('PUTs the file as form data under the file field and answers the stored voice', async () => {
    const stored = { id: 'v1', audioFileName: 'voices/alice/v1.wav' };
    api.on('PUT', AUDIO_URL, stored);
    const clip = new File([new Uint8Array([1, 2, 3])], 'take.wav', { type: 'audio/wav' });

    expect(await new VoicesApi().uploadAudio('my book', 'v1', clip)).toEqual(stored as never);
    const body = api.calls('PUT', AUDIO_URL)[0]?.body as FormData;
    expect(body).toBeInstanceOf(FormData);
    expect([...body.keys()]).toEqual(['file']);
    const sent = body.get('file') as File;
    expect(sent.name).toBe('take.wav');
    expect(sent.size).toBe(3);
  });

  it('a refused upload rejects with the host detail', async () => {
    api.on('PUT', AUDIO_URL, problem(422, 'Reference audio must be 30 s or shorter.'));
    const clip = new File([new Uint8Array([1])], 'long.wav', { type: 'audio/wav' });

    const error = await new VoicesApi().uploadAudio('my book', 'v1', clip).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(422);
    expect((error as ApiError).detail).toContain('30 s or shorter');
  });
});

const GENERATE_URL = '/api/projects/my%20book/characters/alice/voices/v1/generate-audio';

describe('VoicesApi.generateAudio', () => {
  it('carries an unsaved prompt as designPrompt, and sends no body without one', async () => {
    api.on('POST', GENERATE_URL, { audioFileName: 'voices/alice/v1.wav' });

    await new VoicesApi().generateAudio('my book', 'alice', 'v1', 'A warm alto');
    await new VoicesApi().generateAudio('my book', 'alice', 'v1');

    expect(api.calls('POST', GENERATE_URL).map((r) => r.body)).toEqual([
      { designPrompt: 'A warm alto' },
      null,
    ]);
  });
});
