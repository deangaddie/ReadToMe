import { ApiError } from '@app/api';
import { routes } from '@app/app.routes';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { ThemeService } from '@app/theme/theme.service';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { installTooltips } from '@app/ui/tooltip';

// The Angular ErrorHandler that toasts uncaught ApiErrors.
const toast = use(ToastService);
addEventListener('unhandledrejection', (e: PromiseRejectionEvent) => {
  if (e.reason instanceof ApiError) {
    e.preventDefault();
    toast.problem(e.reason.toProblem());
  }
});

// Apply the shared theme before first paint (the Angular app initializer); an unreachable host
// keeps the compiled defaults. The shell registers only after, so nothing renders unthemed.
await use(ThemeService).load();
await import('@app/shell/shell');

const confirm = use(ConfirmService);
use(Router).start(routes, () =>
  confirm.confirm({
    title: 'Discard changes?',
    message: 'You have unsaved changes. Leave this page and lose them?',
    confirmLabel: 'Discard',
    destructive: true,
  }),
);
installTooltips();

// Open the live hub once for the app's lifetime; it reconnects on its own.
use(LiveService).start();
