import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { html } from 'lit-html';
import { installNavigation, settle } from '../../testing/fake-navigation';
import { R2mElement, define } from './element';
import { Router, type RouteDef } from './router';
import { override, resetServices } from './services';
import './outlet';

class ListPage extends R2mElement {
  protected template() {
    return html`<p>list</p>`;
  }
}
define('x-outlet-list', ListPage);

class ProjectPage extends R2mElement {
  protected template() {
    return html`<h1>project</h1>
      <r2m-outlet></r2m-outlet>`;
  }
}
define('x-outlet-project', ProjectPage);

class BookPage extends R2mElement {
  protected template() {
    return html`<p>book</p>`;
  }
}
define('x-outlet-book', BookPage);

let lazyLoads = 0;
const ROUTES: RouteDef[] = [
  { path: 'projects', tag: 'x-outlet-list' },
  {
    path: 'projects/:folder',
    tag: 'x-outlet-project',
    children: [
      { path: 'book', tag: 'x-outlet-book' },
      {
        path: 'cast',
        tag: 'x-outlet-cast',
        load: async () => {
          lazyLoads++;
          class CastPage extends R2mElement {
            protected template() {
              return html`<p>cast</p>`;
            }
          }
          define('x-outlet-cast', CastPage);
        },
      },
    ],
  },
];

async function start(path: string) {
  const nav = installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  await settle();
  return { nav, router, outlet };
}

beforeEach(() => {
  resetServices();
  lazyLoads = 0;
});
afterEach(() => document.body.replaceChildren());

describe('r2m-outlet', () => {
  it('renders the element of its level of the matched chain', async () => {
    const { outlet } = await start('projects');
    expect(outlet.firstElementChild?.localName).toBe('x-outlet-list');
  });

  it('a nested outlet renders the next level', async () => {
    const { outlet } = await start('projects/dune/book');
    const project = outlet.firstElementChild;
    expect(project?.localName).toBe('x-outlet-project');
    expect(project?.querySelector('r2m-outlet')?.firstElementChild?.localName).toBe(
      'x-outlet-book',
    );
  });

  it('swaps the element when the route changes', async () => {
    const { outlet, router } = await start('projects');
    router.navigate('projects/dune/book');
    await settle();
    expect(outlet.firstElementChild?.localName).toBe('x-outlet-project');
  });

  it('keeps the element when only a param changes', async () => {
    const { outlet, router } = await start('projects/dune/book');
    const project = outlet.firstElementChild;
    router.navigate('projects/foundation/book');
    await settle();
    expect(outlet.firstElementChild).toBe(project);
  });

  it('loads a lazy route before creating its element', async () => {
    const { outlet, router } = await start('projects/dune/book');
    expect(customElements.get('x-outlet-cast')).toBeUndefined();
    router.navigate('projects/dune/cast');
    await settle();
    expect(lazyLoads).toBe(1);
    const inner = outlet.querySelector('r2m-outlet');
    expect(inner?.firstElementChild?.localName).toBe('x-outlet-cast');
    expect(inner?.firstElementChild?.textContent).toBe('cast');
  });

  it('renders nothing when no route matches', async () => {
    const { outlet, router } = await start('projects');
    router.navigate('nowhere');
    await settle();
    expect(outlet.childElementCount).toBe(0);
  });
});
