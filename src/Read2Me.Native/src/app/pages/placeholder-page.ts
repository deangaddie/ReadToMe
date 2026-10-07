import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed } from '@app/core/signals';
import { shellContext } from '@app/route-meta';
import { emptyState } from '@app/ui/partials';

/**
 * Stands in for a screen the native app does not have yet: names the screen from the route chain
 * and links to the same path in the Angular app at `/app`, which still has it (spec §8.1).
 */
export class PlaceholderPage extends R2mElement {
  private readonly router = use(Router);
  private readonly context = computed(() => shellContext(this.router.match()));

  protected template() {
    const { title, folder } = this.context();
    const angularHref = `/app/${this.router.path()}`;
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">${title}</h1>
        ${
          folder === undefined
            ? nothing
            : html`<p class="r2m-page-header__subtitle">Project: ${folder}</p>`
        }
      </header>
      ${emptyState(
        {
          icon: 'construction',
          headline: 'This screen has not moved to the native app yet',
          hint: 'The Angular app still has it.',
        },
        html`<a
          class="r2m-button r2m-button--stroked r2m-placeholder-page__link"
          href=${angularHref}
          >Open in the Angular app</a
        >`,
      )}
    `;
  }
}
define('r2m-placeholder-page', PlaceholderPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-placeholder-page': PlaceholderPage;
  }
}
