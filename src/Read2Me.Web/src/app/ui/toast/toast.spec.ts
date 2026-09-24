import { TestBed } from '@angular/core/testing';
import { MatSnackBar, MatSnackBarConfig } from '@angular/material/snack-bar';
import { Toast } from './toast';
import { ToastService } from './toast.service';

describe('ToastService', () => {
  let opened: { component: unknown; config: MatSnackBarConfig | undefined }[];

  beforeEach(() => {
    opened = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: MatSnackBar,
          useValue: {
            openFromComponent: (component: unknown, config?: MatSnackBarConfig) => {
              opened.push({ component, config });
              return { dismiss: vi.fn() };
            },
          },
        },
      ],
    });
  });

  it('opens the r2m-toast component with severity data and severity-based durations', () => {
    const service = TestBed.inject(ToastService);

    service.success('Saved');
    service.error('Boom');

    expect(opened[0]?.component).toBe(Toast);
    expect(opened[0]?.config?.data).toEqual({ severity: 'success', message: 'Saved' });
    expect(opened[0]?.config?.duration).toBe(3000);
    expect(opened[0]?.config?.panelClass).toBe('r2m-toast-panel');
    expect(opened[1]?.config?.data).toEqual({ severity: 'error', message: 'Boom' });
    expect(opened[1]?.config?.duration).toBe(6000);
  });

  it('shows ProblemDetails detail verbatim, falling back to title then a generic message', () => {
    const service = TestBed.inject(ToastService);

    service.problem({ title: 'Conflict', detail: 'Audio still pending for 3 items', status: 409 });
    service.problem({ title: 'Not found', status: 404 });
    service.problem(null);

    expect(opened.map((o) => (o.config?.data as { message: string }).message)).toEqual([
      'Audio still pending for 3 items',
      'Not found',
      'Request failed',
    ]);
    expect(opened.every((o) => (o.config?.data as { severity: string }).severity === 'error')).toBe(
      true,
    );
  });
});

describe('r2m-toast', () => {
  it('renders the message with a severity icon and dismisses through the snackbar ref', async () => {
    const dismiss = vi.fn();
    await TestBed.configureTestingModule({
      providers: [
        { provide: MatSnackBar, useValue: {} },
        {
          provide: (await import('@angular/material/snack-bar')).MAT_SNACK_BAR_DATA,
          useValue: { severity: 'warn', message: 'Book updated elsewhere' },
        },
        {
          provide: (await import('@angular/material/snack-bar')).MatSnackBarRef,
          useValue: { dismiss },
        },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(Toast);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.classList.contains('r2m-toast--warn')).toBe(true);
    expect(el.querySelector('mat-icon')?.textContent).toBe('warning');
    expect(el.querySelector('.r2m-toast__message')?.textContent).toBe('Book updated elsewhere');

    el.querySelector<HTMLButtonElement>('.r2m-toast__dismiss')!.click();
    expect(dismiss).toHaveBeenCalled();
  });
});
