import { describe, expect, it } from 'bun:test';
import { ApiClient, ORIGIN_ID, toApiError, type FetchFn, type SuccessBody } from './api-client';
import type { Schema } from './dtos';
import { ApiError } from './api-error';

/** An ApiClient over a canned response, recording the one request it made. */
function clientReturning(respond: () => Response | Promise<Response>) {
  const requests: { url: string; init: RequestInit }[] = [];
  const fetchFn: FetchFn = async (url, init) => {
    requests.push({ url, init });
    return respond();
  };
  return { api: new ApiClient(fetchFn), requests };
}

const headersOf = (init: RequestInit | undefined) =>
  (init?.headers ?? {}) as Record<string, string>;

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), {
    ...init,
    headers: { 'Content-Type': 'application/json' },
  });

describe('ApiClient', () => {
  it('sends the origin id header and returns the JSON body', async () => {
    const { api, requests } = clientReturning(() => json({ ok: true }));
    const body = await api.untypedGet<{ ok: boolean }>('/api/projects', {
      limit: 5,
      skip: undefined,
    });
    expect(body).toEqual({ ok: true });
    expect(requests[0]?.url).toBe('/api/projects?limit=5');
    expect(requests[0]?.init.method).toBe('GET');
    expect(headersOf(requests[0]?.init)['X-Origin-Id']).toBe(ORIGIN_ID);
  });

  it('repeats a query key once per value of an array', async () => {
    const { api, requests } = clientReturning(() => json({}));
    await api.untypedGet('/api/x', { chapterIds: ['a', 'b'], none: [] });
    expect(requests[0]?.url).toBe('/api/x?chapterIds=a&chapterIds=b');
  });

  it('posts a JSON body with its content type', async () => {
    const { api, requests } = clientReturning(() => json({}));
    await api.untypedPost('/api/settings/themes', { name: 'Dusk' });
    const init = requests[0]?.init;
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{"name":"Dusk"}');
    expect(headersOf(init)['Content-Type']).toBe('application/json');
  });

  it('sends a form as it is, leaving the content type to the browser', async () => {
    const { api, requests } = clientReturning(() => json({}));
    const form = new FormData();
    form.set('file', 'x');
    await api.untypedPostForm('/api/projects', form);
    const init = requests[0]?.init;
    expect(init?.body).toBe(form);
    expect(headersOf(init)['Content-Type']).toBeUndefined();
  });

  it('posts JSON for bytes, answering the body as a blob and a refusal as ApiError', async () => {
    const wav = clientReturning(() => new Response(new Uint8Array([82, 73, 70, 70])));
    const blob = await wav.api.untypedPostForBlob('/api/try', { itemId: 'i' });
    expect(await blob.text()).toBe('RIFF');
    expect(wav.requests[0]?.init.body).toBe('{"itemId":"i"}');
    expect(headersOf(wav.requests[0]?.init)['X-Origin-Id']).toBe(ORIGIN_ID);

    const busy = clientReturning(() =>
      json({ status: 409, detail: 'Busy.' }, { status: 409, statusText: 'Conflict' }),
    );
    const error = await busy.api.untypedPostForBlob('/api/try', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).detail).toBe('Busy.');
  });

  it('rejects an aborted blob request with the abort, not an ApiError', async () => {
    const abort = new AbortController();
    const api = new ApiClient((_url, init) => {
      abort.abort();
      return Promise.reject(init.signal?.reason as Error);
    });
    const error = await api
      .untypedPostForBlob('/api/try', {}, abort.signal)
      .catch((e: unknown) => e);
    expect(error).not.toBeInstanceOf(ApiError);
  });

  it('maps a ProblemDetails response to ApiError with the verbatim detail', async () => {
    const { api } = clientReturning(() =>
      json(
        { title: 'Bad Request', status: 400, detail: 'Theme name is required.', hint: 'x' },
        { status: 400, statusText: 'Bad Request' },
      ),
    );
    const error = await api
      .untypedPost('/api/settings/themes', { name: '' })
      .catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    const apiError = error as ApiError;
    expect(apiError.status).toBe(400);
    expect(apiError.detail).toBe('Theme name is required.');
    expect(apiError.extensions['hint']).toBe('x');
    expect(apiError.toProblem()).toEqual({
      title: 'Bad Request',
      detail: 'Theme name is required.',
      status: 400,
    });
  });

  it('flattens validation problem errors into the detail', async () => {
    const { api } = clientReturning(() =>
      json(
        {
          title: 'One or more validation errors occurred.',
          errors: { name: ['required'], primary: ['bad'] },
        },
        { status: 422, statusText: 'Unprocessable' },
      ),
    );
    const error = (await api.untypedPut('/api/x', {}).catch((e: unknown) => e)) as ApiError;
    expect(error.detail).toBe('name: required; primary: bad');
  });

  it('keeps a non-JSON error body as the detail', async () => {
    const { api } = clientReturning(
      () => new Response('upstream timed out', { status: 502, statusText: 'Bad Gateway' }),
    );
    const error = (await api.untypedGet('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(502);
    expect(error.title).toBe('Bad Gateway');
    expect(error.detail).toBe('upstream timed out');
  });

  it('maps a network failure to status 0 "Host unreachable"', async () => {
    const { api } = clientReturning(() => {
      throw new TypeError('Failed to fetch');
    });
    const error = (await api.untypedDelete('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(0);
    expect(error.title).toBe('Host unreachable');
    expect(error.detail).toBe('Failed to fetch');
  });

  it('resolves undefined for an empty 204', async () => {
    const { api } = clientReturning(() => new Response(null, { status: 204 }));
    expect(await api.untypedDelete('/api/x/1')).toBeUndefined();
  });
});

describe('toApiError', () => {
  it('passes an ApiError through and wraps anything else', () => {
    const original = new ApiError(409, 'Conflict');
    expect(toApiError(original)).toBe(original);
    const wrapped = toApiError(new Error('boom'));
    expect(wrapped.status).toBe(0);
    expect(wrapped.detail).toBe('boom');
  });
});

/** Compile-time equality: `true` only when `A` and `B` are the same type. */
type Equal<A, B> =
  (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
const typeIs = <T extends true>(): T => true as T;

const ok = <B>(body: B) => ({ headers: {}, content: { 'application/json': body } });
interface Empty {
  headers: object;
  content?: never;
}

describe('ApiClient path-typed methods', () => {
  it('fills the path template, encoding each value, and adds the query', async () => {
    const { api, requests } = clientReturning(() => json({}));
    await api.get('/api/projects/{folder}/paragraphs/{paragraphId}/context', {
      path: { folder: 'my book', paragraphId: 'p/1' },
      query: { chapterId: 'c1', before: 2 },
    });
    expect(requests[0]?.url).toBe(
      '/api/projects/my%20book/paragraphs/p%2F1/context?chapterId=c1&before=2',
    );
    expect(requests[0]?.init.method).toBe('GET');
  });

  it('answers the JSON body, typed by the path', async () => {
    const { api } = clientReturning(() =>
      json({ steps: [], headStepId: null, busy: null, restoring: false }),
    );
    const history = await api.get('/api/projects/{folder}/history', {
      path: { folder: 'dracula' },
    });
    typeIs<Equal<typeof history, Schema['HistoryDto']>>();
    expect(history.restoring).toBe(false);
  });

  it('posts a typed JSON body', async () => {
    const { api, requests } = clientReturning(() => json({}));
    await api.post('/api/projects/{folder}/history/{stepId}/restore', {
      path: { folder: 'dracula', stepId: 's1' },
    });
    expect(requests[0]?.url).toBe('/api/projects/dracula/history/s1/restore');
    expect(requests[0]?.init.method).toBe('POST');

    await api.put('/api/settings/themes/{id}', {
      path: { id: 7 },
      body: { id: 7, name: 'Dusk' },
    });
    expect(requests[1]?.init.method).toBe('PUT');
    expect(headersOf(requests[1]?.init)['Content-Type']).toBe('application/json');
  });

  it('rejects a path the host does not serve, a wrong method and missing path values', () => {
    const { api } = clientReturning(() => json({}));
    const never = () => {
      // @ts-expect-error: no such route
      void api.get('/api/projects/{folder}/histories', { path: { folder: 'x' } });
      // @ts-expect-error: History is not deleted
      void api.delete('/api/projects/{folder}/history', { path: { folder: 'x' } });
      // @ts-expect-error: the step id is missing
      void api.get('/api/projects/{folder}/history/{stepId}', { path: { folder: 'x' } });
      void api.post('/api/projects/{folder}/history/{stepId}/restore', {
        path: { folder: 'x', stepId: 's' },
        // @ts-expect-error: a body where none is taken
        body: 1,
      });
    };
    void never;
  });

  it('answers a blob for a blob read and sends a form as it is', async () => {
    const wav = clientReturning(() => new Response(new Uint8Array([82, 73, 70, 70])));
    const blob = await wav.api.postForBlob('/api/projects/{folder}/history/{stepId}/restore', {
      path: { folder: 'd', stepId: 's' },
    });
    expect(await blob.text()).toBe('RIFF');
    expect(wav.requests[0]?.url).toBe('/api/projects/d/history/s/restore');

    const upload = clientReturning(() => json({}));
    const form = new FormData();
    await upload.api.postForm('/api/projects', {}, form);
    expect(upload.requests[0]?.init.body).toBe(form);
  });

  it('infers the union of several 2xx bodies, and void with no content', () => {
    interface Op<R> {
      responses: R;
    }
    typeIs<
      Equal<
        SuccessBody<Op<{ 200: ReturnType<typeof ok<'a'>>; 202: ReturnType<typeof ok<'b'>> }>>,
        'a' | 'b'
      >
    >();
    typeIs<
      Equal<SuccessBody<Op<{ 204: Empty; default: ReturnType<typeof ok<'problem'>> }>>, void>
    >();
    typeIs<Equal<SuccessBody<Op<{ 202: Empty }>>, void>>();
  });
});
