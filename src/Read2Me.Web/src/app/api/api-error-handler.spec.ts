import { ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ToastService } from '@app/ui/toast/toast.service';
import { ApiErrorHandler, unwrapApiError } from './api-error-handler';
import { ApiError } from './api-error';

describe('ApiErrorHandler', () => {
  let problems: unknown[];
  let handler: ErrorHandler;

  beforeEach(() => {
    problems = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: ErrorHandler, useClass: ApiErrorHandler },
        { provide: ToastService, useValue: { problem: (p: unknown) => problems.push(p) } },
      ],
    });
    handler = TestBed.inject(ErrorHandler);
  });

  it('toasts an uncaught 422 ApiError with its verbatim detail', () => {
    handler.handleError(
      ApiError.fromProblem(422, { title: 'Rejected', detail: 'Chapter has no paragraphs.' }, 'x'),
    );
    expect(problems).toEqual([
      { title: 'Rejected', detail: 'Chapter has no paragraphs.', status: 422 },
    ]);
  });

  it('unwraps a zone-style { rejection } wrapper', () => {
    const inner = new ApiError(409, 'Conflict', 'Busy');
    expect(unwrapApiError({ rejection: inner })).toBe(inner);
    handler.handleError({ rejection: inner });
    expect(problems).toHaveLength(1);
  });

  it('leaves non-API errors to the default handler', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    handler.handleError(new Error('plain'));
    expect(problems).toEqual([]);
    expect(spy).toHaveBeenCalled();
    spy.mockRestore();
  });
});
