import { ApiError } from '@app/api';
import { routes } from '@app/app.routes';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { LiveService } from '@app/live/live.service';
import { SetupCheckStore } from '@app/setup-check/setup-check-store';
import { confirmDiscard } from '@app/shared/unsaved-changes';
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
// The Setup check report loads alongside: its first-run flag decides where `/` lands.
await Promise.all([use(ThemeService).load(), use(SetupCheckStore).load()]);
await import('@app/shell/shell');

// The same discard prompt the settings pages ask before switching their editor to another item.
const confirm = use(ConfirmService);
use(Router).start(routes, () => confirmDiscard(confirm));
installTooltips();

// Open the live hub once for the app's lifetime; it reconnects on its own.
use(LiveService).start();
