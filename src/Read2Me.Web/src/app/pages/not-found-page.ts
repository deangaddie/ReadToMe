import { html } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed } from '@app/core/signals';
import { shellContext } from '@app/route-meta';
import { emptyState } from '@app/ui/partials';

/** The page for a path no route matches: says so and links back to the projects shelf. */
export class NotFoundPage extends R2mElement {
  private readonly router = use(Router);
  private readonly heading = computed(() => shellContext(this.router.match()).title);

  protected template() {
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">${this.heading()}</h1>
      </header>
      ${emptyState(
        {
          icon: 'question_mark',
          headline: 'This page does not exist',
          hint: 'Check the address, or start again from the projects shelf.',
        },
        html`<a class="r2m-button r2m-button--stroked r2m-not-found-page__link" href="projects"
          >Go to projects</a
        >`,
      )}
    `;
  }
}
define('r2m-not-found-page', NotFoundPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-not-found-page': NotFoundPage;
  }
}
