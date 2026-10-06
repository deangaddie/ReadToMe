import { afterEach, describe, expect, it } from 'bun:test';
import { type FakeNavigation, installNavigation, settle } from '../../testing/fake-navigation';
import { Router, fill, titleOf, type RouteDef } from './router';

const ROUTES: RouteDef[] = [
  { path: '', redirectTo: 'projects' },
  { path: 'projects', title: 'Projects', tag: 'x-projects' },
  {
    path: 'projects/:folder',
    title: (p) => p['folder'] ?? '',
    tag: 'x-project',
    children: [
      { path: '', redirectTo: 'projects/:folder/book' },
      { path: 'book', title: 'Book', tag: 'x-book' },
      { path: 'settings', title: 'Settings', tag: 'x-settings', guardUnsaved: true },
    ],
  },
];

function start(path: string, confirmLeave: () => Promise<boolean> = async () => true) {
  const nav: FakeNavigation = installNavigation(path);
  const router = new Router();
  router.start(ROUTES, confirmLeave);
  return { nav, router };
}

/** Mounts the guarded leaf's element, holding unsaved edits or not. */
function mountSettings(dirty: boolean): void {
  const leaf = Object.assign(document.createElement('x-settings'), {
    hasUnsavedChanges: () => dirty,
  });
  document.body.append(leaf);
}

afterEach(() => document.body.replaceChildren());

describe('Router', () => {
  it('matches a nested route below the base, with decoded params', () => {
    const { router } = start('projects/my%20book/book');
    expect(router.match()?.chain.map((r) => r.tag)).toEqual(['x-project', 'x-book']);
    expect(router.params()).toEqual({ folder: 'my book' });
    expect(router.path()).toBe('projects/my%20book/book');
  });

  it('ignores a trailing slash', () => {
    const { router } = start('projects/');
    expect(router.match()?.chain.at(-1)?.tag).toBe('x-projects');
  });

  it('matches nothing for an unknown path', () => {
    const { router } = start('nowhere');
    expect(router.match()).toBeNull();
    expect(router.params()).toEqual({});
  });

  it('exposes the query string as a signal', () => {
    const { router } = start('projects?filter=done');
    expect(router.query().get('filter')).toBe('done');
  });

  it('follows a redirect at the root, replacing the history entry', () => {
    const { nav, router } = start('');
    expect(nav.calls).toEqual([{ url: 'http://localhost/app2/projects', history: 'replace' }]);
    expect(router.match()?.chain.at(-1)?.tag).toBe('x-projects');
  });

  it('fills a redirect with the matched params', () => {
    const { router } = start('projects/dune');
    expect(router.url().pathname).toBe('/app2/projects/dune/book');
  });

  it('sets the document title from the chain titles', () => {
    start('projects/dune/book');
    expect(document.title).toBe('Read2Me · dune · Book');
  });

  it('navigate() takes an app-relative path and resolves it against the base', async () => {
    const { nav, router } = start('projects');
    router.navigate('projects/dune/book');
    await settle();
    expect(nav.calls.at(-1)).toEqual({
      url: 'http://localhost/app2/projects/dune/book',
      history: 'push',
    });
    expect(router.params()).toEqual({ folder: 'dune' });
  });

  it('navigate() sets and deletes query params', async () => {
    const { router } = start('projects?filter=done&sort=name');
    router.navigate('projects?filter=done&sort=name', { query: { filter: null, page: '2' } });
    await settle();
    expect(router.url().search).toBe('?sort=name&page=2');
  });

  it('follows a link click inside the base', async () => {
    const { nav, router } = start('projects');
    nav.fire('projects/dune/book');
    await settle();
    expect(router.match()?.chain.at(-1)?.tag).toBe('x-book');
  });

  it('leaves a navigation outside the base to the browser', () => {
    const { nav, router } = start('projects');
    nav.fire('/app/projects');
    nav.fire('/app2x/projects');
    nav.fire('/workspace/dune/cover.jpg');
    expect(nav.passedThrough).toHaveLength(3);
    expect(router.url().pathname).toBe('/app2/projects');
  });

  it('treats the base without its trailing slash as the root', async () => {
    const { nav } = start('projects');
    nav.fire('/app2');
    await settle();
    expect(nav.calls.at(-1)?.url).toBe('http://localhost/app2/projects');
  });

  it('leaves hash changes and non-interceptable navigations alone', () => {
    const { nav } = start('projects');
    nav.fire('projects#top', { hashChange: true });
    nav.fire('projects/dune/book', { canIntercept: false });
    expect(nav.passedThrough).toHaveLength(2);
  });

  describe('unsaved-changes guard', () => {
    it('cancels the navigation and stays when the user declines', async () => {
      let asked = 0;
      const { nav, router } = start('projects/dune/settings', async () => {
        asked++;
        return false;
      });
      mountSettings(true);
      const event = nav.fire('projects');
      await settle();
      expect(event.defaultPrevented).toBe(true);
      expect(asked).toBe(1);
      expect(router.url().pathname).toBe('/app2/projects/dune/settings');
    });

    it('repeats the navigation once the user confirms', async () => {
      const { nav, router } = start('projects/dune/settings', async () => true);
      mountSettings(true);
      nav.fire('projects');
      await settle();
      expect(router.url().pathname).toBe('/app2/projects');
    });

    it('repeats a back/forward through traverseTo', async () => {
      const { nav, router } = start('projects', async () => true);
      router.navigate('projects/dune/settings');
      await settle();
      mountSettings(true);
      const event = nav.fire('projects', { navigationType: 'traverse' });
      await settle();
      expect(event.defaultPrevented).toBe(true);
      expect(router.url().pathname).toBe('/app2/projects');
    });

    it('does not ask when the leaf has nothing unsaved', async () => {
      let asked = 0;
      const { nav, router } = start('projects/dune/settings', async () => {
        asked++;
        return false;
      });
      mountSettings(false);
      nav.fire('projects');
      await settle();
      expect(asked).toBe(0);
      expect(router.url().pathname).toBe('/app2/projects');
    });

    it('holds the tab open on beforeunload only while the leaf has unsaved edits', () => {
      start('projects/dune/settings');
      const unload = () => {
        const event = new Event('beforeunload', { cancelable: true });
        dispatchEvent(event);
        return event.defaultPrevented;
      };
      mountSettings(false);
      expect(unload()).toBe(false);
      document.body.replaceChildren();
      mountSettings(true);
      expect(unload()).toBe(true);
    });

    it('asks again for the next navigation after a confirmed one', async () => {
      let asked = 0;
      const { nav, router } = start('projects', async () => {
        asked++;
        return true;
      });
      for (const round of [1, 2]) {
        router.navigate('projects/dune/settings');
        await settle();
        mountSettings(true);
        nav.fire('projects');
        await settle();
        expect(asked).toBe(round);
        document.body.replaceChildren();
      }
    });

    it('does not ask for a query-only change on the same page', async () => {
      let asked = 0;
      const { nav } = start('projects/dune/settings', async () => {
        asked++;
        return false;
      });
      mountSettings(true);
      const event = nav.fire('projects/dune/settings?tab=llm');
      await settle();
      expect(asked).toBe(0);
      expect(event.defaultPrevented).toBe(false);
    });
  });
});

describe('titleOf', () => {
  it('reads a literal title, a computed one, or none', () => {
    expect(titleOf({ path: 'a', title: 'A' }, {})).toBe('A');
    expect(titleOf({ path: ':x', title: (p) => `<${p['x']}>` }, { x: '1' })).toBe('<1>');
    expect(titleOf({ path: 'a' }, {})).toBe('');
  });
});

describe('fill', () => {
  it('replaces :params with their encoded values', () => {
    expect(fill('projects/:folder/cast/:id', { folder: 'my book', id: '7' })).toBe(
      'projects/my%20book/cast/7',
    );
  });
});
