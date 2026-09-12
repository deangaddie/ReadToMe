import { BreakpointObserver } from '@angular/cdk/layout';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
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
import { EmptyState } from '@app/ui/empty-state/empty-state';
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
 * Application shell (design §5): app bar, nav rail, main outlet, activity bar host and activity
 * drawer host. The activity surfaces are empty hosts here; ticket 14 fills them. Below 900 px the
 * rail becomes a modal drawer and the activity bar collapses to one summary pill.
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
    EmptyState,
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
  readonly drawerOpen = signal(false);

  readonly contextItems = computed(() => contextNavItems(this.context()));
  readonly globalItems = GLOBAL_NAV_ITEMS;
  readonly crumbs = computed(() => breadcrumb(this.context()));
  readonly schemePreference = this.theme.preference;

  /** Live hub connection dot (ticket 07): green connected, amber (re)connecting, red disconnected. */
  readonly connection = this.live.state;
  readonly connectionLabel = this.live.statusLabel;

  constructor() {
    // Design §9: the project shell holds `project:{folder}` while any project route is active;
    // child routes never join. Until ticket 09 lands a dedicated project shell, this is it.
    effect((onCleanup) => {
      const folder = this.context().folder;
      if (!folder) return;
      void this.live.joinProject(folder);
      onCleanup(() => void this.live.leaveProject(folder));
    });
  }

  toggleRail(): void {
    if (this.narrow()) this.railOpen.update((open) => !open);
    else this.rail.toggle();
  }

  closeModalRail(): void {
    if (this.narrow()) this.railOpen.set(false);
  }

  toggleDrawer(): void {
    this.drawerOpen.update((open) => !open);
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
