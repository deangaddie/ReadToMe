/** RFC 7807 body the host returns for every non-2xx (`Results.Problem`, validation problems). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}

/**
 * Every failed HTTP call surfaces as one of these (ticket 05 "ApiError"). `detail` is the host's
 * verbatim ProblemDetails detail, shown as-is in alerts and toasts (design §8); `extensions` keeps
 * anything else the host attached (e.g. `audioRemainingCount` on an assembly 409).
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string | undefined;
  readonly extensions: Record<string, unknown>;

  constructor(
    status: number,
    title: string,
    detail?: string,
    extensions: Record<string, unknown> = {},
  ) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.extensions = extensions;
  }

  static fromProblem(status: number, body: unknown, fallbackTitle: string): ApiError {
    if (body && typeof body === 'object') {
      const {
        type,
        title,
        status: bodyStatus,
        detail,
        instance,
        errors,
        ...rest
      } = body as ProblemDetails;
      const flatErrors = errors
        ? Object.entries(errors)
            .map(([field, messages]) => `${field}: ${messages.join(', ')}`)
            .join('; ')
        : undefined;
      return new ApiError(bodyStatus ?? status, title ?? fallbackTitle, detail ?? flatErrors, {
        ...rest,
        ...(type ? { type } : {}),
        ...(instance ? { instance } : {}),
        ...(errors ? { errors } : {}),
      });
    }
    const text = typeof body === 'string' && body.trim() ? body.trim() : undefined;
    return new ApiError(status, fallbackTitle, text);
  }

  /** Shape the toast service understands. */
  toProblem(): { title: string; detail?: string; status: number } {
    return this.detail === undefined
      ? { title: this.title, status: this.status }
      : { title: this.title, detail: this.detail, status: this.status };
  }
}
