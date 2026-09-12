import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiError } from './api-error';

/**
 * Stable per-tab origin id (ticket 05 / spec D6): sent as `X-Origin-Id` on every call so the hub
 * can echo it in mutation receipts and the client recognises its own writes.
 */
export const ORIGIN_ID: string =
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;

export type QueryParams = Record<string, string | number | boolean | null | undefined>;

/**
 * The one HTTP wrapper feature code goes through (ticket 05; this slice is the part ticket 04 needs).
 * JSON in and out, same-origin relative URLs (the dev proxy and the /app host both keep the API
 * same-origin), every non-2xx mapped to an {@link ApiError} from the ProblemDetails body.
 * Nothing outside `app/api/` imports HttpClient directly (ESLint rule).
 */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  get<T>(url: string, params?: QueryParams): Promise<T> {
    return this.run<T>(
      this.http.get<T>(url, { params: toParams(params), headers: this.headers() }),
    );
  }

  post<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.run<T>(
      this.http.post<T>(url, body ?? null, { params: toParams(params), headers: this.headers() }),
    );
  }

  put<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.run<T>(
      this.http.put<T>(url, body ?? null, { params: toParams(params), headers: this.headers() }),
    );
  }

  delete<T = void>(url: string, params?: QueryParams): Promise<T> {
    return this.run<T>(
      this.http.delete<T>(url, { params: toParams(params), headers: this.headers() }),
    );
  }

  patch<T>(url: string, body?: unknown, params?: QueryParams): Promise<T> {
    return this.run<T>(
      this.http.patch<T>(url, body ?? null, { params: toParams(params), headers: this.headers() }),
    );
  }

  postForm<T>(url: string, form: FormData): Promise<T> {
    return this.run<T>(this.http.post<T>(url, form, { headers: this.headers() }));
  }

  putForm<T>(url: string, form: FormData): Promise<T> {
    return this.run<T>(this.http.put<T>(url, form, { headers: this.headers() }));
  }

  private headers(): HttpHeaders {
    return new HttpHeaders({ 'X-Origin-Id': ORIGIN_ID });
  }

  private async run<T>(request: import('rxjs').Observable<T>): Promise<T> {
    try {
      return await firstValueFrom(request, { defaultValue: undefined as T });
    } catch (error) {
      throw toApiError(error);
    }
  }
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (error instanceof HttpErrorResponse) {
    const fallback = error.status === 0 ? 'Host unreachable' : error.statusText || 'Request failed';
    return ApiError.fromProblem(error.status, error.error, fallback);
  }
  return new ApiError(0, 'Request failed', error instanceof Error ? error.message : String(error));
}

function toParams(params?: QueryParams): HttpParams {
  let result = new HttpParams();
  if (!params) return result;
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined) result = result.set(key, String(value));
  }
  return result;
}
