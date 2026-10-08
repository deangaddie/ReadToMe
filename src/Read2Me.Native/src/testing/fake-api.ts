import { ApiClient } from '@app/api/api-client';
import { override } from '@app/core/services';

/** Answers a routed request: a JSON body, or a `Response` for a status other than 200; a promise of either holds the reply. */
export type FakeRoute = (body: unknown, url: URL) => unknown;

export interface FakeRequest {
  method: string;
  path: string;
  body: unknown;
}

/**
 * The HttpTestingController stand-in: an `ApiClient` over a fetch that answers from routes keyed by
 * method and path, recording every request. Unrouted requests get a 404 ProblemDetails. Install it
 * with {@link FakeApi.install} before the service under test calls `use(ApiClient)`.
 */
export class FakeApi {
  readonly requests: FakeRequest[] = [];
  readonly #routes = new Map<string, FakeRoute>();

  /** Routes `method path` to a handler, or to a canned JSON body (undefined → 204). */
  on(method: string, path: string, respond: FakeRoute): this;
  on(method: string, path: string, body: unknown): this;
  on(method: string, path: string, respond: unknown): this {
    this.#routes.set(
      `${method.toUpperCase()} ${path}`,
      typeof respond === 'function' ? (respond as FakeRoute) : () => respond,
    );
    return this;
  }

  /** The requests made to one route, oldest first. */
  calls(method: string, path: string): FakeRequest[] {
    return this.requests.filter((r) => r.method === method.toUpperCase() && r.path === path);
  }

  install(): ApiClient {
    const client = new ApiClient(async (input, init) => {
      const url = new URL(input, 'http://localhost');
      const method = (init.method ?? 'GET').toUpperCase();
      const body = typeof init.body === 'string' ? (JSON.parse(init.body) as unknown) : init.body;
      this.requests.push({ method, path: url.pathname, body });
      const route = this.#routes.get(`${method} ${url.pathname}`);
      if (!route) {
        return Response.json(
          {
            title: 'Not Found',
            status: 404,
            detail: `no fake route for ${method} ${url.pathname}`,
          },
          { status: 404 },
        );
      }
      const result = await route(body, url);
      if (result instanceof Response) return result;
      if (result === undefined) return new Response(null, { status: 204 });
      return Response.json(result);
    });
    override(ApiClient, client);
    return client;
  }
}

/** A failed request: the fake answers with this status and ProblemDetails. */
export function problem(status: number, detail: string): Response {
  return Response.json({ title: 'Error', status, detail }, { status });
}
