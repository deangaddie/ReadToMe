import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiClient, ORIGIN_ID } from './api-client';
import { ApiError } from './api-error';

describe('ApiClient', () => {
  let api: ApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends the origin id header and returns the JSON body', async () => {
    const call = api.get<{ ok: boolean }>('/api/projects', { limit: 5, skip: undefined });
    const req = http.expectOne((r) => r.url === '/api/projects');
    expect(req.request.headers.get('X-Origin-Id')).toBe(ORIGIN_ID);
    expect(req.request.params.get('limit')).toBe('5');
    expect(req.request.params.has('skip')).toBe(false);
    req.flush({ ok: true });
    await expect(call).resolves.toEqual({ ok: true });
  });

  it('maps a ProblemDetails response to ApiError with the verbatim detail', async () => {
    const call = api.post('/api/settings/themes', { name: '' });
    http
      .expectOne('/api/settings/themes')
      .flush(
        { title: 'Bad Request', status: 400, detail: 'Theme name is required.', hint: 'x' },
        { status: 400, statusText: 'Bad Request' },
      );
    const error = await call.catch((e: unknown) => e);
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
    const call = api.put('/api/x', {});
    http.expectOne('/api/x').flush(
      {
        title: 'One or more validation errors occurred.',
        errors: { name: ['required'], primary: ['bad'] },
      },
      { status: 422, statusText: 'Unprocessable' },
    );
    const error = (await call.catch((e: unknown) => e)) as ApiError;
    expect(error.detail).toBe('name: required; primary: bad');
  });

  it('maps a network failure to status 0 "Host unreachable"', async () => {
    const call = api.delete('/api/x');
    http.expectOne('/api/x').error(new ProgressEvent('error'), { status: 0 });
    const error = (await call.catch((e: unknown) => e)) as ApiError;
    expect(error.status).toBe(0);
    expect(error.title).toBe('Host unreachable');
  });

  it('resolves void for an empty 204', async () => {
    const call = api.delete('/api/x/1');
    http.expectOne('/api/x/1').flush(null, { status: 204, statusText: 'No Content' });
    await expect(call).resolves.toBeNull();
  });
});
