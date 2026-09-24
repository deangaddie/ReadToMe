import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { RouteMeta } from '@app/route-meta';

/**
 * Stand-in page for every route whose real content lands in a later slice. Reads the route's
 * `RouteMeta` so the shell, rail and breadcrumb behave exactly as they will once the page exists.
 */
@Component({
  selector: 'app-placeholder-page',
  imports: [PageHeader, EmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header [title]="meta().title" [subtitle]="subtitle()" />
    <r2m-empty-state icon="construction" [headline]="headline()" [hint]="hint()" />
  `,
})
export class PlaceholderPage {
  private readonly route = inject(ActivatedRoute);
  private readonly data = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  private readonly params = toSignal(this.route.params, {
    initialValue: this.route.snapshot.params,
  });

  protected readonly meta = computed(() => this.data() as RouteMeta);

  protected readonly subtitle = computed(() => {
    const folder = this.params()['folder'] as string | undefined;
    return folder ? `Project: ${folder}` : undefined;
  });

  protected readonly headline = computed(() => {
    const slice = this.meta().slice;
    return slice ? `Coming in slice ${String(slice).padStart(2, '0')}` : 'Not available';
  });

  protected readonly hint = computed(() =>
    this.meta().slice
      ? 'The route, rail group and breadcrumb are wired; the page content arrives with that ticket.'
      : 'There is nothing at this address.',
  );
}
