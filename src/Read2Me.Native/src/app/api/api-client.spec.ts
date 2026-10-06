import { describe, expect, it } from 'bun:test';
import { ApiClient, ORIGIN_ID, toApiError, type FetchFn } from './api-client';
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
    const body = await api.get<{ ok: boolean }>('/api/projects', { limit: 5, skip: undefined });
    expect(body).toEqual({ ok: true });
    expect(requests[0]?.url).toBe('/api/projects?limit=5');
    expect(requests[0]?.init.method).toBe('GET');
    expect(headersOf(requests[0]?.init)['X-Origin-Id']).toBe(ORIGIN_ID);
  });

  it('posts a JSON body with its content type', async () => {
    const { api, requests } = clientReturning(() => json({}));
    await api.post('/api/settings/themes', { name: 'Dusk' });
    const init = requests[0]?.init;
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe('{"name":"Dusk"}');
    expect(headersOf(init)['Content-Type']).toBe('application/json');
  });

  it('sends a form as it is, leaving the content type to the browser', async () => {
    const { api, requests } = clientReturning(() => json({}));
    const form = new FormData();
    form.set('file', 'x');
    await api.postForm('/api/projects', form);
    const init = requests[0]?.init;
    expect(init?.body).toBe(form);
    expect(headersOf(init)['Content-Type']).toBeUndefined();
  });

  it('maps a ProblemDetails response to ApiError with the verbatim detail', async () => {
    const { api } = clientReturning(() =>
      json(
        { title: 'Bad Request', status: 400, detail: 'Theme name is required.', hint: 'x' },
        { status: 400, statusText: 'Bad Request' },
      ),
    );
    const error = await api.post('/api/settings/themes', { name: '' }).catch((e: unknown) => e);
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
    const error = (await api.put('/api/x', {}).catch((e: unknown) => e)) as ApiError;
    expect(error.detail).toBe('name: required; primary: bad');
  });

  it('keeps a non-JSON error body as the detail', async () => {
    const { api } = clientReturning(
      () => new Response('upstream timed out', { status: 502, statusText: 'Bad Gateway' }),
    );
    const error = (await api.get('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(502);
    expect(error.title).toBe('Bad Gateway');
    expect(error.detail).toBe('upstream timed out');
  });

  it('maps a network failure to status 0 "Host unreachable"', async () => {
    const { api } = clientReturning(() => {
      throw new TypeError('Failed to fetch');
    });
    const error = (await api.delete('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(0);
    expect(error.title).toBe('Host unreachable');
    expect(error.detail).toBe('Failed to fetch');
  });

  it('resolves undefined for an empty 204', async () => {
    const { api } = clientReturning(() => new Response(null, { status: 204 }));
    expect(await api.delete('/api/x/1')).toBeUndefined();
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
