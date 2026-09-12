import { ErrorHandler, Injectable, Injector, inject } from '@angular/core';
import { ToastService } from '@app/ui/toast/toast.service';
import { ApiError } from './api-error';

/**
 * Global fallback for {@link ApiError}s nobody caught (design §8): a failed request whose promise
 * rejection reaches Angular's error handler becomes an error toast showing the ProblemDetails
 * `detail`. Components that want inline handling simply `catch` and the toast never fires.
 * Anything that is not an ApiError goes to the default handler (console) unchanged.
 */
@Injectable()
export class ApiErrorHandler extends ErrorHandler {
  private readonly injector = inject(Injector);

  override handleError(error: unknown): void {
    const apiError = unwrapApiError(error);
    if (!apiError) {
      super.handleError(error);
      return;
    }
    // Resolved lazily: the handler is created before the overlay/snack-bar graph exists.
    this.injector.get(ToastService).problem(apiError.toProblem());
  }
}

/** Finds the ApiError inside a raw rejection or a wrapped one (`{ rejection }` from zone.js). */
export function unwrapApiError(error: unknown): ApiError | null {
  if (error instanceof ApiError) return error;
  if (error && typeof error === 'object' && 'rejection' in error) {
    const inner = (error as { rejection: unknown }).rejection;
    if (inner instanceof ApiError) return inner;
  }
  return null;
}
