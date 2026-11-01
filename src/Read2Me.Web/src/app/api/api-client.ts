import { ApiError } from './api-error';
import type { paths } from './schema';
import { readsAtStep } from './step-view';

/** The HTTP methods the host maps. */
export type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete';

/** The paths the host serves `M` on: a route renamed, removed or not taking `M` isn't one. */
export type PathsFor<M extends HttpMethod> = {
  [P in keyof paths]: [NonNullable<paths[P][M]>] extends [never] ? never : P;
}[keyof paths];

/** The operation the host serves at `P` for `M`. */
export type Operation<P extends keyof paths, M extends HttpMethod> = NonNullable<paths[P][M]>;

type SuccessStatus = 200 | 201 | 202 | 203 | 204 | 206;

/** A response's JSON body, or `void` for one with no content (a 204, a bare 202). */
type BodyOf<R> = R extends { content: { 'application/json': infer B } } ? B : void;

/**
 * What an operation answers on success: the union of its 2xx JSON bodies (`Ok<T>` + `Accepted<U>`
 * → `T | U`), `void` when it has none. Non-2xx answers are an {@link ApiError}, never a value.
 */
export type SuccessBody<O> = O extends { responses: infer R }
  ? { [S in keyof R & SuccessStatus]: BodyOf<R[S]> }[keyof R & SuccessStatus]
  : never;

type ParamsOf<O> = O extends { parameters: infer P } ? P : never;

type PathPart<O> = ParamsOf<O> extends { path: infer V } ? { path: V } : { path?: never };

type QueryPart<O> =
  ParamsOf<O> extends { query: infer Q }
    ? { query: Q }
    : ParamsOf<O> extends { query?: infer Q }
      ? [NonNullable<Q>] extends [never]
        ? { query?: never }
        : { query?: NonNullable<Q> }
      : { query?: never };

type RequestBodyOf<O> = O extends { requestBody?: infer B } ? NonNullable<B> : never;

type BodyPart<O> = [RequestBodyOf<O>] extends [never]
  ? { body?: never }
  : O extends { requestBody: { content: { 'application/json': infer B } } }
    ? { body: B }
    : O extends { requestBody?: { content: { 'application/json': infer B } } }
      ? { body?: B }
      : { body?: never };

/** A call's `{ path, query, body }`, typed by its operation. */
export type CallInit<O> = PathPart<O> & QueryPart<O> & BodyPart<O>;

/** The init argument: optional when nothing in it is required. */
type InitArg<O> = object extends CallInit<O> ? [init?: CallInit<O>] : [init: CallInit<O>];

/** The init of a form upload or blob read: the route's values; the body is the form or JSON as given. */
type RouteInit<O> = PathPart<O> & QueryPart<O>;

/**
 * Stable per-tab origin id (ticket 05 / spec D6): sent as `X-Origin-Id` on every call so the hub
 * can echo it in mutation receipts and the client recognises its own writes.
 */
export const ORIGIN_ID: string = crypto.randomUUID();

export type FetchFn = (input: string, init: RequestInit) => Promise<Response>;

/** An array value repeats its key once per value (`?ids=a&ids=b`), as ASP.NET binds a `Guid[]`. */
export type QueryParams = Record<
  string,
  string | number | boolean | readonly string[] | null | undefined
>;

/**
 * The one HTTP wrapper feature code goes through: JSON in and out, same-origin relative URLs,
 * every non-2xx mapped to an {@link ApiError} from the ProblemDetails body. `fetch` replaces
 * HttpClient; tests replace `fetch` itself via the constructor.
 */
export class ApiClient {
  constructor(private readonly fetchFn: FetchFn = (input, init) => fetch(input, init)) {}

  /**
   * A typed read: `path` is a route template the host serves (`'/api/projects/{folder}/history'`),
   * `init` its `{ path, query }` values, and the answer is typed by the document. A Project state
   * read of the project being viewed at a History step carries `?at=` ({@link readsAtStep}).
   */
  get<P extends PathsFor<'get'>>(
    path: P,
    ...[init]: InitArg<Operation<P, 'get'>>
  ): Promise<SuccessBody<Operation<P, 'get'>>> {
    const url = fillPath(path, init?.path);
    const query = init?.query as QueryParams | undefined;
    const at = readsAtStep(url);
    return this.send('GET', url, undefined, at === null ? query : { ...query, at });
  }

  post<P extends PathsFor<'post'>>(
    path: P,
    ...[init]: InitArg<Operation<P, 'post'>>
  ): Promise<SuccessBody<Operation<P, 'post'>>> {
    return this.sendByPath('POST', path, init);
  }

  put<P extends PathsFor<'put'>>(
    path: P,
    ...[init]: InitArg<Operation<P, 'put'>>
  ): Promise<SuccessBody<Operation<P, 'put'>>> {
    return this.sendByPath('PUT', path, init);
  }

  patch<P extends PathsFor<'patch'>>(
    path: P,
    ...[init]: InitArg<Operation<P, 'patch'>>
  ): Promise<SuccessBody<Operation<P, 'patch'>>> {
    return this.sendByPath('PATCH', path, init);
  }

  delete<P extends PathsFor<'delete'>>(
    path: P,
    ...[init]: InitArg<Operation<P, 'delete'>>
  ): Promise<SuccessBody<Operation<P, 'delete'>>> {
    return this.sendByPath('DELETE', path, init);
  }

  /** A multipart upload: the route is typed by path; the form isn't in the document, so it goes as given. */
  postForm<P extends PathsFor<'post'>>(
    path: P,
    init: RouteInit<Operation<P, 'post'>>,
    form: FormData,
  ): Promise<SuccessBody<Operation<P, 'post'>>> {
    return this.send(
      'POST',
      fillPath(path, init.path),
      form,
      init.query as QueryParams | undefined,
    );
  }

  putForm<P extends PathsFor<'put'>>(
    path: P,
    init: RouteInit<Operation<P, 'put'>>,
    form: FormData,
  ): Promise<SuccessBody<Operation<P, 'put'>>> {
    return this.send('PUT', fillPath(path, init.path), form, init.query as QueryParams | undefined);
  }

  /**
   * A JSON POST answered with bytes (a rendered WAV, say), typed by path for its route and body.
   * Aborting `signal` drops the request; the promise then rejects with the `AbortError`, not an
   * {@link ApiError}, so callers can tell a stop from a failure. A non-2xx answer is an
   * {@link ApiError} from its ProblemDetails body.
   */
  postForBlob<P extends PathsFor<'post'>>(
    path: P,
    init: CallInit<Operation<P, 'post'>>,
    signal?: AbortSignal,
  ): Promise<Blob> {
    return this.sendForBlob(
      withParams(fillPath(path, init.path), init.query as QueryParams | undefined),
      init.body ?? null,
      signal,
    );
  }

  private sendByPath<T>(method: string, path: string, init: unknown): Promise<T> {
    const {
      path: values,
      query,
      body,
    } = (init ?? {}) as { path?: object; query?: object; body?: unknown };
    return this.send<T>(
      method,
      fillPath(path, values),
      body ?? null,
      query as QueryParams | undefined,
    );
  }

  private async sendForBlob(url: string, body: unknown, signal?: AbortSignal): Promise<Blob> {
    const response = await this.fetchFn(url, {
      method: 'POST',
      headers: { 'X-Origin-Id': ORIGIN_ID, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: signal ?? null,
    }).catch((error: unknown) => {
      if (signal?.aborted) throw error;
      throw new ApiError(
        0,
        'Host unreachable',
        error instanceof Error ? error.message : String(error),
      );
    });
    if (!response.ok) {
      const text = await response.text();
      throw ApiError.fromProblem(
        response.status,
        text ? safeJson(text) : undefined,
        response.statusText || 'Request failed',
      );
    }
    return response.blob();
  }

  private async send<T>(
    method: string,
    url: string,
    body?: unknown,
    params?: QueryParams,
  ): Promise<T> {
    const headers: Record<string, string> = {
      'X-Origin-Id': ORIGIN_ID,
      Accept: 'application/json',
    };
    let payload: BodyInit | undefined;
    if (body instanceof FormData) payload = body;
    else if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      payload = JSON.stringify(body);
    }
    let response: Response;
    try {
      response = await this.fetchFn(withParams(url, params), {
        method,
        headers,
        body: payload ?? null,
      });
    } catch (error) {
      throw new ApiError(
        0,
        'Host unreachable',
        error instanceof Error ? error.message : String(error),
      );
    }
    const text = await response.text();
    const json: unknown = text ? safeJson(text) : undefined;
    if (!response.ok)
      throw ApiError.fromProblem(response.status, json, response.statusText || 'Request failed');
    return json as T;
  }
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  return new ApiError(0, 'Request failed', error instanceof Error ? error.message : String(error));
}

function withParams(url: string, params?: QueryParams): string {
  if (!params) return url;
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (Array.isArray(value)) for (const v of value) search.append(key, v);
    else if (value !== null && value !== undefined) search.set(key, String(value));
  }
  const query = search.toString();
  return query ? `${url}?${query}` : url;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/** The route template with each `{name}` replaced by its value, URI-encoded. */
function fillPath(template: string, values: unknown): string {
  const byName = (values ?? {}) as Record<string, string | number>;
  return template.replace(/\{([^}]+)\}/g, (_, name: string) => {
    const value = byName[name];
    if (value === undefined) throw new Error(`No value for {${name}} in ${template}.`);
    return encodeURIComponent(String(value));
  });
}
