import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  ActivatedRoute,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { LiveService } from '@app/live/live.service';
import { RouteMeta } from '@app/route-meta';
import { ActivityBar } from '@app/activity/activity-bar';
import { ActivityDrawer } from '@app/activity/activity-drawer';
import { ActivityStore } from '@app/activity/activity-store';
import { ProjectTitles } from './project-titles';
import { RailState } from './rail-state';
import { SchemePreference, ThemeService } from '@app/theme/theme.service';
import {
  GLOBAL_NAV_ITEMS,
  ShellContext,
  breadcrumb,
  contextNavItems,
  toShellContext,
} from './shell-nav';

const NARROW_QUERY = '(max-width: 899.98px)';

/**
 * Application shell (design §5): app bar, nav rail, main outlet, activity bar and activity drawer
 * (ticket 14: both read {@link ActivityStore}). Below 900 px the rail becomes a modal drawer and
 * the activity bar collapses to one summary pill.
 */
@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatToolbarModule,
    MatSidenavModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatTooltipModule,
    ActivityBar,
    ActivityDrawer,
  ],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'app-shell' },
})
export class Shell {
  private readonly router = inject(Router);
  private readonly rootRoute = inject(ActivatedRoute);
  private readonly breakpoints = inject(BreakpointObserver);
  private readonly rail = inject(RailState);
  private readonly theme = inject(ThemeService);
  private readonly live = inject(LiveService);
  private readonly projectTitles = inject(ProjectTitles);
  private readonly activity = inject(ActivityStore);

  /** Deepest activated route's data + params, refreshed on every navigation. */
  private readonly context = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      startWith(null),
      map(() => this.readContext()),
    ),
    { initialValue: this.readContext() },
  );

  readonly narrow = toSignal(
    this.breakpoints.observe(NARROW_QUERY).pipe(map((state) => state.matches)),
    { initialValue: this.breakpoints.isMatched(NARROW_QUERY) },
  );

  readonly railExpanded = this.rail.expanded;
  /** Modal rail visibility on narrow screens; ignored when the rail is docked. */
  readonly railOpen = signal(false);
  /** The activity drawer (ticket 14); pills and the ▲ open it through the store. */
  readonly drawerOpen = this.activity.drawerOpen;

  readonly contextItems = computed(() => contextNavItems(this.context()));
  readonly globalItems = GLOBAL_NAV_ITEMS;
  /** The project crumb reads the title the project shell loaded, the folder name until then. */
  readonly crumbs = computed(() => {
    const ctx = this.context();
    return breadcrumb(ctx, ctx.folder ? this.projectTitles.titles()[ctx.folder] : undefined);
  });
  readonly schemePreference = this.theme.preference;

  /** Live hub connection dot (ticket 07): green connected, amber (re)connecting, red disconnected. */
  readonly connection = this.live.state;
  readonly connectionLabel = this.live.statusLabel;

  toggleRail(): void {
    if (this.narrow()) this.railOpen.update((open) => !open);
    else this.rail.toggle();
  }

  closeModalRail(): void {
    if (this.narrow()) this.railOpen.set(false);
  }

  toggleDrawer(): void {
    this.activity.toggleDrawer();
  }

  /** Theme quick menu (design §5): writes the shared selection through the API. */
  setScheme(preference: SchemePreference): void {
    void this.theme.setPreference(preference);
  }

  private readContext(): ShellContext {
    let route = this.rootRoute.snapshot;
    while (route.firstChild) route = route.firstChild;
    const meta = route.data as Partial<RouteMeta>;
    return toShellContext(
      { section: meta.section ?? 'projects', title: meta.title ?? '' },
      route.params as Record<string, string | undefined>,
    );
  }
}
