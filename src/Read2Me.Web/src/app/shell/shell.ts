import { html, nothing } from 'lit-html';
import { ActivityStore } from '@app/activity/activity-store';
import { R2mElement, define } from '@app/core/element';
import { Router, fill, titleOf } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { dayLabel, restoreCost, stepTime } from '@app/history/history-rows';
import { HistorySheetState } from '@app/history/history-sheet-state';
import { LiveService } from '@app/live/live.service';
import { shellContext } from '@app/route-meta';
import { SetupCheckStore } from '@app/setup-check/setup-check-store';
import { SETUP_CHECK_PATH, failedLabel } from '@app/setup-check/surfacing';
import { type SchemePreference, ThemeService } from '@app/theme/theme.service';
import type { IconName } from '@app/ui/icons';
import { icon, spinner } from '@app/ui/partials';
import { type GuideEntry, ProjectGuide } from './project-guide';
import { RailState } from './rail-state';
import { GLOBAL_NAV_ITEMS, type NavItem, contextNavItems, isActive } from './shell-nav';
import '@app/core/outlet';
import '@app/activity/activity-bar';
import '@app/activity/activity-drawer';
import '@app/history/history-sheet';
import logoUrl from '../../assets/images/logo-48.png';
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
 * Application shell (design §5): app bar with breadcrumbs from the matched route chain, the Setup
 * check shield, the live connection dot and the theme quick menu, with the Setup check's start
 * banner under it (both read {@link SetupCheckStore}; without a Workspace the main area shows the
 * Setup check page for every route instead of the outlet); the nav rail with the context group for "here" and the
 * global group; the main outlet; the activity bar below and the activity drawer beside the content
 * (both read {@link ActivityStore}); on a project's pages, the History button and the History sheet
 * docked beside the content (both read {@link HistorySheetState}). While the project is viewed at a
 * History step, the amber banner sits above the body, the page is greyed and the next-step pill is
 * hidden; the page stays browsable, and each edit control reads the project's read-only flag
 * (`ProjectStore.readOnly`) to render disabled (ticket 32). Below 900 px the rail becomes a modal drawer, the History sheet covers
 * the page body (the banner stays above it) and the activity bar collapses to one summary pill.
 */
export class AppShell extends R2mElement {
  private readonly router = use(Router);
  private readonly rail = use(RailState);
  private readonly theme = use(ThemeService);
  private readonly live = use(LiveService);
  private readonly activity = use(ActivityStore);
  private readonly guide = use(ProjectGuide);
  private readonly historySheet = use(HistorySheetState);
  private readonly setupCheck = use(SetupCheckStore);

  /** The activity drawer (design §5); pills and the ▲ open it through the store. */
  readonly drawerOpen = this.activity.drawerOpen;

  private readonly context = computed(() => shellContext(this.router.match()));
  private readonly contextItems = computed(() => contextNavItems(this.context()));

  /** The route's project's History, for the app-bar button and the sheet; null off a project's pages. */
  private readonly history = computed(() => {
    const entry = this.historySheet.current();
    return entry && entry.folder === this.context().folder ? entry.store : null;
  });

  /** Whether the route's project is viewed at a History step, and that step's row. */
  private readonly viewing = computed(() => (this.history()?.viewing() ?? null) !== null);
  private readonly viewedStep = computed(() => this.history()?.viewedStep() ?? null);

  /**
   * The next-step pill's entry: the route's project, on any of its pages but the Overview (spec §4).
   * Hidden while viewing a History step, which it would send you to work on.
   */
  private readonly nextStep = computed<GuideEntry | null>(() => {
    const entry = this.guide.current();
    const leaf = this.router.match()?.chain.at(-1)?.tag;
    if (!entry || entry.folder !== this.context().folder || leaf === 'r2m-overview-page')
      return null;
    if (this.viewing()) return null;
    return entry;
  });

  /**
   * One crumb per titled route, linking all but the last. An index child (`path: ''`) shares its
   * parent's path, so its title names the page (tab title, placeholder) but adds no crumb.
   */
  private readonly crumbs = computed<Crumb[]>(() => {
    if (this.setupCheck.noWorkspace()) return [{ title: 'Setup check', href: SETUP_CHECK_PATH }];
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
    // No-Workspace mode renders the Setup check page in place of the outlet, so its chunk loads here.
    this.effect(() => {
      if (this.setupCheck.noWorkspace())
        void import('@app/pages/settings/setup-check/setup-check-page');
    });
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
    // Browser tests wait on this rather than on a transport detail like the WebSocket.
    this.setAttribute('data-live-state', connection);
    const connectionLabel = this.live.statusLabel();
    const preference = this.theme.preference();
    // The page body while viewing a History step: greyed, still browsable; its edit controls are off.
    const viewing = this.viewing();
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
        <a class="shell__brand" href="projects"
          ><img src=${logoUrl} alt="" width="24" height="24" />Read2Me</a
        >

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

        ${this.nextStepPill()} ${this.historyButton()} ${this.setupShield()}

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

      ${this.setupBanner()} ${this.viewingBanner()}

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
        <main class="shell__main ${viewing ? 'shell__main--viewing' : ''}">
          ${
            // Without a Workspace every route shows the Setup check, which says what to fix.
            this.setupCheck.noWorkspace()
              ? html`<r2m-setup-check-page></r2m-setup-check-page>`
              : html`<r2m-outlet></r2m-outlet>`
          }
        </main>
        ${
          this.history() && this.historySheet.open()
            ? html`<aside class="shell__history" aria-labelledby="history-sheet-title">
                <r2m-history-sheet .store=${this.history()}></r2m-history-sheet>
              </aside>`
            : nothing
        }
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

  /**
   * The next step and its primary action (guidance-primitives spec §4): a click runs it through the
   * project's StepRunner, the Overview row's path (confirms included), with its checkpoint pills
   * beside it. A running or blocked step names its chip and opens the Overview instead. Under
   * 900 px the CSS keeps the main button alone.
   */
  private nextStepPill() {
    const entry = this.nextStep();
    if (!entry) return nothing;
    const { next, runner } = entry;
    const disabled = runner.busy() !== null;
    const openOverview = () => runner.openOverview();
    const main =
      next.kind === 'allDone'
        ? html`<span class="shell__next-main shell__next-main--done">${next.label}</span>`
        : html`<button
            type="button"
            class="r2m-button shell__next-main"
            data-tooltip=${next.tooltip ?? nothing}
            ?disabled=${disabled}
            @click=${
              next.kind === 'waiting'
                ? openOverview
                : () => void runner.act({ step: next.step, action: next.action })
            }
          >
            <span class="shell__next-label">${next.label}</span>
          </button>`;
    const pills =
      next.kind !== 'allDone'
        ? next.pills.map(
            (p) =>
              html`<button
                type="button"
                class="r2m-button r2m-button--stroked shell__next-pill"
                data-checkpoint=${p.id}
                ?disabled=${disabled}
                @click=${() => void runner.checkpoint({ step: next.step, pill: p.id })}
              >
                ${p.label}
              </button>`,
          )
        : nothing;
    return html`<div
      class="shell__next shell__next--${next.tint}"
      role="group"
      aria-label="Next step"
    >
      ${main} ${pills}
      <button
        type="button"
        class="r2m-icon-button shell__next-overview"
        aria-label="Open Overview"
        data-tooltip="Open Overview"
        @click=${openOverview}
      >
        ${icon('list')}
      </button>
    </div>`;
  }

  /**
   * The amber banner while viewing a History step (spec, "Viewing a step"): it names the step and
   * its time, says what restoring it costs, and holds Restore this step and Back to current. Full
   * width above the body, so the narrow-width sheet that covers the body leaves it in view.
   * Restore has no confirm: the cost is stated before the click, and nothing is lost (the later
   * steps stay in History), so Restore waits only for the cost to be known.
   */
  private viewingBanner() {
    const store = this.history();
    const step = this.viewedStep();
    if (!store || !this.viewing()) return nothing;
    const name = step
      ? `#${step.sequence} ${step.label}${step.note ? ` (${step.note})` : ''}`
      : 'a past step';
    const when = step
      ? ` · ${dayLabel(new Date(step.startedAt), new Date())} ${stepTime(step)}`
      : '';
    const cost = store.restorePreview();
    const restoring = store.restoring();
    return html`<div class="shell__viewing" role="status">
      ${icon('lock')}
      <span class="shell__viewing-text"
        ><strong>Viewing ${name}</strong>${step?.summary ? ` — ${step.summary}` : ''}${when}. The
        project as it was after this step; read-only.
        ${restoring ? 'The project is restoring.' : (store.viewError() ?? restoreCost(cost))}</span
      >
      <button
        type="button"
        class="r2m-button r2m-button--stroked shell__viewing-restore"
        ?disabled=${cost === null || restoring}
        @click=${() => void store.restore()}
      >
        Restore this step
      </button>
      <button
        type="button"
        class="r2m-button r2m-button--stroked shell__viewing-back"
        @click=${() => store.backToCurrent()}
      >
        Back to current
      </button>
    </div>`;
  }

  /**
   * The start banner (Setup check spec): red, full width under the app bar, on a start where a
   * check failed. Dismissing it lasts until the host starts again; it clears by itself once
   * nothing fails.
   */
  private setupBanner() {
    if (!this.setupCheck.bannerVisible()) return nothing;
    const failed = this.setupCheck.failedCount();
    return html`<div class="shell__setup-banner" role="alert">
      ${icon('error')}
      <span class="shell__setup-banner-text"
        ><strong>${failedLabel(failed)}.</strong> Some steps will not work until they are
        fixed.</span
      >
      <a class="r2m-button r2m-button--stroked" href=${SETUP_CHECK_PATH}>Open Setup check</a>
      <button
        type="button"
        class="r2m-icon-button shell__setup-banner-dismiss"
        aria-label="Dismiss"
        data-tooltip="Dismiss until the app restarts"
        @click=${() => this.setupCheck.dismissBanner()}
      >
        ${icon('close')}
      </button>
    </div>`;
  }

  /**
   * The app-bar shield: always shown, linking to the Setup check; red with a count when something
   * failed, a spinner with "Full check n/N" while a Full check runs.
   */
  private setupShield() {
    const shield = this.setupCheck.shield();
    return html`<a
      class="r2m-icon-button shell__setup-shield shell__setup-shield--${shield.status}"
      href=${SETUP_CHECK_PATH}
      aria-label=${shield.label}
      data-tooltip=${shield.label}
      >${shield.status === 'busy' ? spinner(20) : icon('health_and_safety')}${
        shield.failed > 0
          ? html`<span class="shell__setup-shield-count" aria-hidden="true">${shield.failed}</span>`
          : nothing
      }</a
    >`;
  }

  private historyButton() {
    if (!this.history()) return nothing;
    const open = this.historySheet.open();
    return html`<button
      type="button"
      class="r2m-icon-button shell__history-toggle ${open ? 'shell__history-toggle--on' : ''}"
      aria-label="History"
      aria-pressed=${open}
      data-tooltip="History"
      @click=${() => this.historySheet.toggle()}
    >
      ${icon('history')}
    </button>`;
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
