import { html, nothing } from 'lit-html';
import { ActivityStore } from '@app/activity/activity-store';
import { R2mElement, define } from '@app/core/element';
import { Router, fill, titleOf } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { shellContext } from '@app/route-meta';
import { type SchemePreference, ThemeService } from '@app/theme/theme.service';
import type { IconName } from '@app/ui/icons';
import { icon } from '@app/ui/partials';
import { RailState } from './rail-state';
import { GLOBAL_NAV_ITEMS, type NavItem, contextNavItems, isActive } from './shell-nav';
import '@app/core/outlet';
import '@app/activity/activity-bar';
import '@app/activity/activity-drawer';
import shellCss from './shell.css' with { type: 'text' };

adoptStyles(shellCss);

const NARROW_QUERY = '(max-width: 899.98px)';
const THEME_MENU_ID = 'r2m-theme-menu';

const SCHEME_ICONS: Record<SchemePreference, IconName> = {
  light: 'light_mode',
  dark: 'dark_mode',
  system: 'brightness_auto',
};

interface Crumb {
  title: string;
  href: string;
}

/**
 * Application shell (design §5): app bar with breadcrumbs from the matched route chain, the live
 * connection dot and the theme quick menu; the nav rail with the context group for "here" and the
 * global group; the main outlet; the activity bar below and the activity drawer beside the content
 * (both read {@link ActivityStore}). Below 900 px the rail becomes a modal drawer and the activity
 * bar collapses to one summary pill.
 */
export class AppShell extends R2mElement {
  private readonly router = use(Router);
  private readonly rail = use(RailState);
  private readonly theme = use(ThemeService);
  private readonly live = use(LiveService);
  private readonly activity = use(ActivityStore);

  /** The activity drawer (design §5); pills and the ▲ open it through the store. */
  readonly drawerOpen = this.activity.drawerOpen;

  private readonly context = computed(() => shellContext(this.router.match()));
  private readonly contextItems = computed(() => contextNavItems(this.context()));

  /**
   * One crumb per titled route, linking all but the last. An index child (`path: ''`) shares its
   * parent's path, so its title names the page (tab title, placeholder) but adds no crumb.
   */
  private readonly crumbs = computed<Crumb[]>(() => {
    const match = this.router.match();
    if (!match) return [];
    const segments: string[] = [];
    const crumbs: Crumb[] = [];
    for (const route of match.chain) {
      if (route.path) segments.push(fill(route.path, match.params));
      const title = titleOf(route, match.params);
      if (title && route.path) crumbs.push({ title, href: segments.join('/') || './' });
    }
    return crumbs;
  });

  private readonly narrow = signal(false);
  /** Modal rail visibility on narrow screens; ignored when the rail is docked. */
  private readonly railOpen = signal(false);

  protected override connected(): void {
    if (typeof matchMedia === 'function') {
      const query = matchMedia(NARROW_QUERY);
      this.narrow.set(query.matches);
      const onChange = (e: MediaQueryListEvent) => this.narrow.set(e.matches);
      query.addEventListener('change', onChange);
      this.onDisconnect(() => query.removeEventListener('change', onChange));
    }
  }

  private readonly toggleRail = (): void => {
    if (this.narrow()) this.railOpen.update((open) => !open);
    else this.rail.toggle();
  };

  private readonly closeModalRail = (): void => {
    if (this.narrow()) this.railOpen.set(false);
  };

  /** Theme quick menu (design §5): writes the shared selection through the API. */
  private setScheme(preference: SchemePreference): void {
    this.querySelector<HTMLElement>(`#${THEME_MENU_ID}`)?.hidePopover();
    void this.theme.setPreference(preference);
  }

  protected template() {
    const crumbs = this.crumbs();
    const narrow = this.narrow();
    const expanded = narrow || this.rail.expanded();
    const connection = this.live.state();
    const connectionLabel = this.live.statusLabel();
    const preference = this.theme.preference();
    return html`
      <header class="shell__appbar">
        <button
          type="button"
          class="r2m-icon-button shell__menu"
          @click=${this.toggleRail}
          aria-label="Toggle navigation"
          data-tooltip="Toggle navigation"
        >
          ${icon('menu')}
        </button>
        <a class="shell__brand" href="projects">Read2Me</a>

        <nav class="shell__crumbs" aria-label="Breadcrumb">
          ${crumbs.map((crumb, i) => {
            const last = i === crumbs.length - 1;
            return html`${
              last
                ? html`<span class="shell__crumb shell__crumb--current" aria-current="page"
                    >${crumb.title}</span
                  >`
                : html`<a class="shell__crumb" href=${crumb.href}>${crumb.title}</a>`
            }${last ? nothing : html`<span class="shell__crumb-sep" aria-hidden="true">›</span>`}`;
          })}
        </nav>

        <span class="shell__spacer"></span>

        <span
          class="shell__conn shell__conn--${connection}"
          role="status"
          aria-label=${connectionLabel}
          data-tooltip=${connectionLabel}
        >
          <span class="shell__conn-dot" aria-hidden="true"></span>
          <span class="shell__conn-text">live</span>
        </span>

        <button
          type="button"
          class="r2m-icon-button shell__theme"
          popovertarget=${THEME_MENU_ID}
          aria-label="Theme"
          data-tooltip="Theme"
        >
          ${icon(SCHEME_ICONS[preference])}
        </button>
        <div id=${THEME_MENU_ID} popover class="r2m-menu shell__theme-menu">
          ${this.schemeItem('light', 'Light')} ${this.schemeItem('dark', 'Dark')}
          ${this.schemeItem('system', 'Follow system')}
        </div>

        <a
          class="r2m-icon-button shell__settings"
          href="settings"
          aria-label="Settings"
          data-tooltip="Settings"
          >${icon('settings')}</a
        >
      </header>

      <div class="shell__body">
        ${
          narrow && this.railOpen()
            ? html`<div class="shell__scrim" @click=${this.closeModalRail}></div>`
            : nothing
        }
        <aside
          class="shell__rail ${expanded ? 'shell__rail--expanded' : ''} ${
            narrow ? (this.railOpen() ? 'shell__rail--open' : 'shell__rail--closed') : ''
          }"
        >
          <nav class="shell__nav" aria-label="Sections">
            ${
              this.contextItems().length
                ? html`<ul class="shell__nav-group shell__nav-group--context">
                    ${this.contextItems().map((item) => this.navItem(item, expanded))}
                  </ul>`
                : nothing
            }
            <span class="shell__nav-spacer"></span>
            <ul class="shell__nav-group shell__nav-group--global">
              ${GLOBAL_NAV_ITEMS.map((item) => this.navItem(item, expanded))}
            </ul>
          </nav>
        </aside>
        <main class="shell__main"><r2m-outlet></r2m-outlet></main>
        ${
          this.drawerOpen()
            ? html`<aside class="shell__drawer" aria-label="Activity">
                <!-- Mounted only while open so a stream tab's hub group is left when the drawer closes. -->
                <r2m-activity-drawer></r2m-activity-drawer>
              </aside>`
            : nothing
        }
      </div>

      <footer class="shell__activity" aria-label="Background activity">
        <r2m-activity-bar .narrow=${narrow}></r2m-activity-bar>
      </footer>
    `;
  }

  private schemeItem(preference: SchemePreference, label: string) {
    const current = this.theme.preference() === preference;
    return html`<button
      type="button"
      class="r2m-menu__item"
      role="menuitemradio"
      aria-checked=${current}
      data-scheme=${preference}
      @click=${() => this.setScheme(preference)}
    >
      ${icon(SCHEME_ICONS[preference])}<span>${label}</span>
    </button>`;
  }

  private navItem(item: NavItem, expanded: boolean) {
    const active = isActive(item, this.router.path());
    return html`<li>
      <a
        class="shell__nav-item ${active ? 'shell__nav-item--active' : ''}"
        href=${item.link}
        aria-current=${active ? 'page' : nothing}
        data-tooltip=${expanded ? nothing : item.label}
        @click=${this.closeModalRail}
      >
        ${icon(item.icon)}<span class="shell__nav-label">${item.label}</span>
      </a>
    </li>`;
  }
}
define('app-root', AppShell);

declare global {
  interface HTMLElementTagNameMap {
    'app-root': AppShell;
  }
}
