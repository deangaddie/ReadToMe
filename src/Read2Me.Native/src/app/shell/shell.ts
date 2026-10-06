import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router, fill, titleOf } from '@app/core/router';
import { use } from '@app/core/services';
import { computed } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { icon } from '@app/ui/partials';
import '@app/core/outlet';
import shellCss from './shell.css' with { type: 'text' };

adoptStyles(shellCss);

/**
 * The app shell frame: an app bar with the brand and breadcrumbs (from the matched route chain's
 * titles), then the root outlet.
 */
export class AppShell extends R2mElement {
  private readonly router = use(Router);

  private readonly crumbs = computed(() => {
    const match = this.router.match();
    if (!match) return [];
    const segments: string[] = [];
    return match.chain
      .map((route) => {
        if (route.path) segments.push(fill(route.path, match.params));
        return { title: titleOf(route, match.params), href: segments.join('/') || './' };
      })
      .filter((c) => c.title);
  });

  protected template() {
    const crumbs = this.crumbs();
    return html`
      <header class="shell__appbar">
        <a class="shell__brand" href="./">${icon('graphic_eq')} Read2Me</a>
        <nav class="shell__crumbs" aria-label="Breadcrumbs">
          ${crumbs.map(
            (c, i) =>
              html`${i ? html`<span aria-hidden="true">›</span>` : nothing}
              ${
                i === crumbs.length - 1
                  ? html`<span aria-current="page">${c.title}</span>`
                  : html`<a href=${c.href}>${c.title}</a>`
              }`,
          )}
        </nav>
      </header>
      <main class="shell__main"><r2m-outlet></r2m-outlet></main>
    `;
  }
}
define('app-root', AppShell);

declare global {
  interface HTMLElementTagNameMap {
    'app-root': AppShell;
  }
}
