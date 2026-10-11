import { ApiError } from './api-error';

/**
 * Stable per-tab origin id (ticket 05 / spec D6): sent as `X-Origin-Id` on every call so the hub
 * can echo it in mutation receipts and the client recognises its own writes.
 */
export const ORIGIN_ID: string = crypto.randomUUID();

export type FetchFn = (input: string, init: RequestInit) => Promise<Response>;

export type QueryParams = Record<string, string | number | boolean | null | undefined>;

/**
 * The one HTTP wrapper feature code goes through: JSON in and out, same-origin relative URLs,
 * every non-2xx mapped to an {@link ApiError} from the ProblemDetails body. `fetch` replaces
 * HttpClient; tests replace `fetch` itself via the constructor.
 */
export class ApiClient {
  constructor(private readonly fetchFn: FetchFn = (input, init) => fetch(input, init)) {}

  get<T>(url: string, params?: QueryParams): Promise<T> {
    return this.send<T>('GET', url, undefined, params);
  }

  post<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.send<T>('POST', url, body ?? null, params);
  }

  put<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.send<T>('PUT', url, body ?? null, params);
  }

  patch<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.send<T>('PATCH', url, body ?? null, params);
  }

  delete<T = void>(url: string, params?: QueryParams): Promise<T> {
    return this.send<T>('DELETE', url, undefined, params);
  }

  postForm<T>(url: string, form: FormData): Promise<T> {
    return this.send<T>('POST', url, form);
  }

  putForm<T>(url: string, form: FormData): Promise<T> {
    return this.send<T>('PUT', url, form);
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
    if (value !== null && value !== undefined) search.set(key, String(value));
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
