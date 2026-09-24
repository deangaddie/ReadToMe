/**
 * Static route data every route declares (design §4, §5). The shell reads the deepest activated
 * route's data to pick the nav-rail context group and build the breadcrumb; placeholder pages read
 * it to say which slice will replace them. Child routes inherit it (`paramsInheritanceStrategy: 'always'`).
 */
export type Section = 'projects' | 'project' | 'settings' | 'styleguide';

export interface RouteMeta {
  section: Section;
  /** Page title; also the last breadcrumb segment. */
  title: string;
  /** Ticket number that builds the real page (placeholder pages only). */
  slice?: number;
}

export function routeMeta(meta: RouteMeta): RouteMeta {
  return meta;
}
