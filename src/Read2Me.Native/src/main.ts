import { ApiError } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { use } from '@app/core/services';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { installTooltips } from '@app/ui/tooltip';
import '@app/shell/shell';

/**
 * The route table. Each level names the element that renders it and lazily loads its chunk;
 * `guardUnsaved` replaces `canDeactivate: [unsavedChangesGuard]`. No screen is ported yet, so every
 * path renders the placeholder inside the shell frame.
 */
const routes: RouteDef[] = [
  {
    path: '*',
    tag: 'r2m-placeholder-page',
    load: () => import('@app/pages/placeholder-page'),
  },
];

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

// The Angular ErrorHandler that toasts uncaught ApiErrors.
const toast = use(ToastService);
addEventListener('unhandledrejection', (e: PromiseRejectionEvent) => {
  if (e.reason instanceof ApiError) {
    e.preventDefault();
    toast.problem(e.reason.toProblem());
  }
});
