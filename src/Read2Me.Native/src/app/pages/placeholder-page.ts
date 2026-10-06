import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { emptyState } from '@app/ui/partials';

/**
 * Stands in for a screen the native app does not have yet, and links to the same path in the
 * Angular app at `/app`, which still has it.
 */
export class PlaceholderPage extends R2mElement {
  private readonly router = use(Router);

  protected template() {
    const angularHref = `/app/${this.router.path()}`;
    return emptyState(
      {
        icon: 'construction',
        headline: 'This screen has not moved to the native app yet',
        hint: 'The Angular app still has it.',
      },
      html`<a class="r2m-button r2m-button--stroked r2m-placeholder-page__link" href=${angularHref}
        >Open in the Angular app</a
      >`,
    );
  }
}
define('r2m-placeholder-page', PlaceholderPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-placeholder-page': PlaceholderPage;
  }
}
