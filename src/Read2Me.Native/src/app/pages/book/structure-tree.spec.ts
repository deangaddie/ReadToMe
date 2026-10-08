import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { NodeStatusSummary } from '@app/live/live-messages';
import { FakeTimers } from '../../../testing/fake-timers';
import type { TreeNode } from './book-tree';
import { NODE_MENU_OPEN, type NodeMenuRequest } from './node-menu';
import { type StructureTree, type TreeRow, flattenTree } from './structure-tree';
import './structure-tree';

/**
 * The hand-written ARIA tree (spec §2.3 point 8): a flat list of visible rows with the tree
 * attributes, a roving tabindex and the CDK `TreeKeyManager` keys. Expansion belongs to the
 * caller, so the DOM only changes once `expandedIds` does; the specs flip it by hand where a key
 * asks for it. The node menu is the shared `r2m-node-menu`: the tree only enriches its open.
 */
function node(
  id: string,
  level: TreeNode['level'],
  children: TreeNode[] = [],
  overrides: Partial<TreeNode> = {},
): TreeNode {
  return {
    id,
    level,
    title: id,
    rawTitle: id,
    isFirst: true,
    isLast: true,
    expandable: level !== 'chapter',
    loaded: true,
    loadTarget: { level, id },
    children,
    ...overrides,
  };
}

/** Marks each sibling's position, as `buildTree` does. */
function siblings(...nodes: TreeNode[]): TreeNode[] {
  return nodes.map((n, i) => ({ ...n, isFirst: i === 0, isLast: i === nodes.length - 1 }));
}

const chapters = (...ids: string[]) => siblings(...ids.map((id) => node(id, 'chapter')));

/** Two volumes: the first with two parts (the first part open to its chapters), the second unloaded. */
const NODES: TreeNode[] = siblings(
  node(
    'v1',
    'volume',
    siblings(
      node('p1', 'part', chapters('Alpha', 'Bravo')),
      node('p2', 'part', chapters('Charlie')),
    ),
  ),
  node('v2', 'volume', [], { loaded: false }),
);

let tree: StructureTree;

async function mount(
  options: Partial<
    Pick<StructureTree, 'nodes' | 'expandedIds' | 'statuses' | 'currentChapterId' | 'locked'>
  > = {},
) {
  tree = document.createElement('r2m-structure-tree');
  tree.nodes = options.nodes ?? NODES;
  tree.expandedIds = options.expandedIds ?? new Set(['v1', 'p1']);
  tree.statuses = options.statuses ?? {};
  tree.currentChapterId = options.currentChapterId ?? null;
  tree.locked = options.locked ?? false;
  document.body.append(tree);
  await tree.rendered();
  return tree;
}

const items = () => Array.from(tree.querySelectorAll<HTMLElement>('[role="treeitem"]'));
const ids = () => items().map((i) => i.dataset['nodeId']);
const item = (id: string) => tree.querySelector<HTMLElement>(`[data-node-id="${id}"]`)!;
const press = (key: string, init: KeyboardEventInit = {}) =>
  document.activeElement!.dispatchEvent(
    new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
  );

function expansions() {
  const log: { id: string; expanded: boolean }[] = [];
  tree.addEventListener('expanded-change', (e) => {
    const { node: n, expanded } = (e as CustomEvent<{ node: TreeNode; expanded: boolean }>).detail;
    log.push({ id: n.id, expanded });
  });
  return log;
}

/** Applies an expansion request the way the page does through the store. */
function applyExpansions(log: { id: string; expanded: boolean }[]) {
  const next = new Set(tree.expandedIds);
  for (const { id, expanded } of log) {
    if (expanded) next.add(id);
    else next.delete(id);
  }
  tree.expandedIds = next;
}

beforeEach(() => {
  document.body.replaceChildren();
});
afterEach(() => document.body.replaceChildren());

describe('flattenTree', () => {
  it('lists the visible rows in order with their depth, set size and position', () => {
    const rows = flattenTree(NODES, new Set(['v1', 'p1']));
    expect(
      rows.map((r: TreeRow) => [r.node.id, r.depth, r.setSize, r.position, r.parent?.id ?? null]),
    ).toEqual([
      ['v1', 0, 2, 1, null],
      ['p1', 1, 2, 1, 'v1'],
      ['Alpha', 2, 2, 1, 'p1'],
      ['Bravo', 2, 2, 2, 'p1'],
      ['p2', 1, 2, 2, 'v1'],
      ['v2', 0, 2, 2, null],
    ]);
    expect(flattenTree(NODES, new Set()).map((r) => r.node.id)).toEqual(['v1', 'v2']);
  });
});

describe('r2m-structure-tree', () => {
  it('renders the ARIA tree: levels, set sizes, positions, expansion and the current chapter', async () => {
    await mount({
      currentChapterId: 'Bravo',
      statuses: { p1: status({ attributionRemaining: 3 }) },
    });
    expect(tree.querySelector('[role="tree"]')?.getAttribute('aria-label')).toBe('Book structure');
    expect(ids()).toEqual(['v1', 'p1', 'Alpha', 'Bravo', 'p2', 'v2']);
    const attrs = (id: string, name: string) => item(id).getAttribute(name);
    expect([
      attrs('v1', 'aria-level'),
      attrs('v1', 'aria-setsize'),
      attrs('v1', 'aria-posinset'),
    ]).toEqual(['1', '2', '1']);
    expect([
      attrs('Bravo', 'aria-level'),
      attrs('Bravo', 'aria-setsize'),
      attrs('Bravo', 'aria-posinset'),
    ]).toEqual(['3', '2', '2']);
    expect(attrs('v1', 'aria-expanded')).toBe('true');
    expect(attrs('p2', 'aria-expanded')).toBe('false');
    expect(item('Bravo').hasAttribute('aria-expanded')).toBe(false);
    expect(attrs('Bravo', 'aria-current')).toBe('true');
    expect(item('Bravo').classList.contains('tree__node--current')).toBe(true);
    expect(item('Alpha').hasAttribute('aria-current')).toBe(false);
    expect(item('p1').querySelector('.r2m-count-badge--attribution')?.textContent).toContain('3');
    expect(item('v2').querySelector('.tree__loading')).toBeNull();
    // Titles are the display titles; the current chapter is the one tab stop.
    expect(
      Array.from(tree.querySelectorAll('.tree__title')).map((t) => t.textContent?.trim()),
    ).toEqual(['v1', 'p1', 'Alpha', 'Bravo', 'p2', 'v2']);
    expect(items().map((i) => i.tabIndex)).toEqual([-1, -1, -1, 0, -1, -1]);
  });

  it('an expanded node whose children are not loaded yet shows a loading mark', async () => {
    await mount({ expandedIds: new Set(['v2']) });
    expect(item('v2').getAttribute('aria-expanded')).toBe('true');
    expect(item('v2').querySelector('.tree__loading')).not.toBeNull();
    expect(ids()).toEqual(['v1', 'v2']);
  });

  it('a click on a chapter selects it; the toggle asks the caller to expand or collapse', async () => {
    await mount();
    const selected: string[] = [];
    tree.addEventListener('select-chapter', (e) =>
      selected.push((e as CustomEvent<string>).detail),
    );
    const log = expansions();

    item('Alpha').querySelector<HTMLElement>('.tree__title')!.click();
    expect(selected).toEqual(['Alpha']);
    expect(document.activeElement).toBe(item('Alpha'));

    // A click on a parent's title only focuses it; its chevron toggles.
    item('p2').querySelector<HTMLElement>('.tree__title')!.click();
    expect(document.activeElement).toBe(item('p2'));
    expect(log).toEqual([]);
    item('p2').querySelector<HTMLElement>('.tree__toggle')!.click();
    item('v1').querySelector<HTMLElement>('.tree__toggle')!.click();
    expect(log).toEqual([
      { id: 'p2', expanded: true },
      { id: 'v1', expanded: false },
    ]);
    // Nothing moved until the caller says so.
    expect(ids()).toEqual(['v1', 'p1', 'Alpha', 'Bravo', 'p2', 'v2']);
    applyExpansions(log);
    await tree.rendered();
    expect(ids()).toEqual(['v1', 'v2']);
  });

  it('arrows move the roving focus without wrapping; Home and End jump to the ends', async () => {
    await mount();
    item('v1').focus();
    press('ArrowDown');
    expect(document.activeElement).toBe(item('p1'));
    expect(items().map((i) => i.tabIndex)).toEqual([-1, 0, -1, -1, -1, -1]);
    press('End');
    expect(document.activeElement).toBe(item('v2'));
    press('ArrowDown');
    expect(document.activeElement).toBe(item('v2'));
    press('Home');
    expect(document.activeElement).toBe(item('v1'));
    press('ArrowUp');
    expect(document.activeElement).toBe(item('v1'));
  });

  it('Right expands a collapsed node, then steps into its first child; Left goes to the parent, then collapses', async () => {
    await mount();
    const log = expansions();
    item('p2').focus();
    press('ArrowRight');
    expect(log).toEqual([{ id: 'p2', expanded: true }]);
    applyExpansions(log);
    await tree.rendered();
    expect(document.activeElement).toBe(item('p2'));
    press('ArrowRight');
    expect(document.activeElement).toBe(item('Charlie'));
    // A chapter has nothing to expand.
    press('ArrowRight');
    expect(document.activeElement).toBe(item('Charlie'));

    press('ArrowLeft');
    expect(document.activeElement).toBe(item('p2'));
    press('ArrowLeft');
    expect(log.at(-1)).toEqual({ id: 'p2', expanded: false });
    applyExpansions(log);
    await tree.rendered();
    expect(document.activeElement).toBe(item('p2'));
    expect(ids()).not.toContain('Charlie');
    // A collapsed root has no parent to go to.
    item('v2').focus();
    press('ArrowLeft');
    expect(document.activeElement).toBe(item('v2'));
  });

  it('Right on an unloaded node asks for it to be expanded and loaded, rather than stepping in', async () => {
    await mount();
    const log = expansions();
    item('v2').focus();
    press('ArrowRight');
    expect(log).toEqual([{ id: 'v2', expanded: true }]);
    applyExpansions(log);
    await tree.rendered();
    press('ArrowRight');
    expect(document.activeElement).toBe(item('v2'));
    expect(log).toHaveLength(1);
  });

  it('Enter and Space select a chapter; on a parent they do nothing (as in Angular)', async () => {
    await mount();
    const selected: string[] = [];
    tree.addEventListener('select-chapter', (e) =>
      selected.push((e as CustomEvent<string>).detail),
    );
    const log = expansions();
    item('Bravo').focus();
    press('Enter');
    press(' ');
    expect(selected).toEqual(['Bravo', 'Bravo']);
    item('v1').focus();
    press('Enter');
    expect(log).toEqual([]);
    expect(selected).toHaveLength(2);
  });

  it('* expands every collapsed sibling at the focused level', async () => {
    await mount({ expandedIds: new Set(['v1']) });
    const log = expansions();
    item('p1').focus();
    press('*');
    expect(log).toEqual([
      { id: 'p1', expanded: true },
      { id: 'p2', expanded: true },
    ]);
  });

  it('type-ahead focuses the next row whose title starts with the letters typed', async () => {
    await mount();
    item('v1').focus();
    const timers = new FakeTimers().install();
    try {
      press('b');
      press('r');
      await timers.advance(200);
      expect(document.activeElement).toBe(item('Bravo'));
      press('p');
      await timers.advance(200);
      expect(document.activeElement).toBe(item('p2'));
      // Wraps round.
      press('p');
      await timers.advance(200);
      expect(document.activeElement).toBe(item('p1'));
    } finally {
      timers.restore();
    }
  });

  it('keeps the focused row as the tab stop across a re-render, and falls back when it goes', async () => {
    await mount();
    item('p2').focus();
    tree.statuses = { p2: status({ audioRemaining: 1 }) };
    await tree.rendered();
    expect(items().map((i) => i.tabIndex)).toEqual([-1, -1, -1, -1, 0, -1]);
    tree.nodes = siblings(node('v1', 'volume', siblings(node('p1', 'part', chapters('Alpha')))));
    await tree.rendered();
    expect(items().map((i) => i.tabIndex)).toEqual([0, -1, -1]);
  });

  describe('selection checkboxes (tickets 12 and 13)', () => {
    const box = (id: string) => item(id).querySelector<HTMLInputElement>('.tree__select');

    it('appear only with a selection kind, labelled for it, and mirror the node states', async () => {
      await mount();
      expect(tree.querySelectorAll('.tree__select').length).toBe(0);

      tree.selection = 'paragraphs';
      tree.nodeStates = { v1: 'indeterminate', p1: 'checked' };
      await tree.rendered();
      expect(tree.querySelectorAll('.tree__select').length).toBe(items().length);
      expect(box('p1')!.getAttribute('aria-label')).toBe('Select paragraphs of p1');
      expect([box('v1')!.checked, box('v1')!.indeterminate]).toEqual([false, true]);
      expect([box('p1')!.checked, box('p1')!.indeterminate]).toEqual([true, false]);
      expect([box('Alpha')!.checked, box('Alpha')!.indeterminate]).toEqual([false, false]);
      // Not a tab stop: the row is, and Space on the row opens a chapter, not the box.
      expect(box('p1')!.tabIndex).toBe(-1);
    });

    it('ticking reports the node and the direction, and does not open the chapter', async () => {
      await mount();
      tree.selection = 'items';
      await tree.rendered();
      const toggles: { id: string; on: boolean }[] = [];
      const opened: string[] = [];
      tree.addEventListener('toggle-node', (e) => {
        const { node: n, on } = (e as CustomEvent<{ node: TreeNode; on: boolean }>).detail;
        toggles.push({ id: n.id, on });
      });
      tree.addEventListener('select-chapter', (e) =>
        opened.push((e as CustomEvent<string>).detail),
      );

      // A click toggles the box and fires `change`; it stops at the box, since the row's own
      // click handler would open the chapter.
      const alpha = box('Alpha')!;
      alpha.click();
      alpha.click();
      expect(toggles).toEqual([
        { id: 'Alpha', on: true },
        { id: 'Alpha', on: false },
      ]);
      expect(opened).toEqual([]);
    });
  });

  describe('node menu', () => {
    it('every node has a trigger, which the tree enriches with its own focus return and action handler', async () => {
      await mount();
      const actions: { id: string; action: string }[] = [];
      tree.addEventListener('node-action', (e) => {
        const { node: n, action } = (e as CustomEvent<{ node: TreeNode; action: string }>).detail;
        actions.push({ id: n.id, action });
      });
      let request: NodeMenuRequest | undefined;
      document.body.addEventListener(
        NODE_MENU_OPEN,
        (e) => (request = (e as CustomEvent<NodeMenuRequest>).detail),
      );

      const trigger = item('p1').querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!;
      expect(trigger.getAttribute('aria-label')).toBe('Actions for p1');
      expect(trigger.tabIndex).toBe(-1);
      trigger.click();
      expect(request?.target).toEqual({
        kind: 'part',
        id: 'p1',
        text: 'p1',
        isFirst: true,
        isLast: false,
      });
      expect(request?.returnTo).toBe(item('p1'));
      request?.onAction?.('select-unprocessed');
      expect(actions).toEqual([{ id: 'p1', action: 'select-unprocessed' }]);
    });

    it('the selection kind reaches the target, and a busy node or a locked editor disables the trigger', async () => {
      await mount({
        statuses: {
          p1: status({ attributionQueued: 2 }),
          p2: status({ attributionProcessing: true }),
        },
      });
      tree.selection = 'items';
      await tree.rendered();
      let request: NodeMenuRequest | undefined;
      document.body.addEventListener(
        NODE_MENU_OPEN,
        (e) => (request = (e as CustomEvent<NodeMenuRequest>).detail),
      );
      const trigger = (id: string) =>
        item(id).querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!;
      expect(trigger('p1').disabled).toBe(true);
      expect(trigger('p2').disabled).toBe(true);
      expect(trigger('v1').disabled).toBe(false);
      trigger('v1').click();
      expect(request?.target.selection).toBe('items');

      tree.locked = true;
      await tree.rendered();
      expect(trigger('v1').disabled).toBe(true);
    });

    it('Shift+F10 and the ContextMenu key open the focused node menu', async () => {
      await mount();
      const opened: string[] = [];
      document.body.addEventListener(NODE_MENU_OPEN, (e) =>
        opened.push((e as CustomEvent<NodeMenuRequest>).detail.target.id),
      );
      item('Alpha').focus();
      press('F10', { shiftKey: true });
      press('ContextMenu');
      expect(opened).toEqual(['Alpha', 'Alpha']);
    });

    it('keys from inside a node trigger are not tree keys', async () => {
      await mount();
      const selected: string[] = [];
      tree.addEventListener('select-chapter', (e) =>
        selected.push((e as CustomEvent<string>).detail),
      );
      const trigger = item('Alpha').querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!;
      trigger.focus();
      press('Enter');
      press('ArrowDown');
      expect(selected).toEqual([]);
      expect(document.activeElement).toBe(trigger);
    });
  });
});

function status(overrides: Partial<NodeStatusSummary>): NodeStatusSummary {
  return {
    attributionRemaining: 0,
    audioRemaining: 0,
    review: 0,
    attributionProcessing: false,
    attributionQueued: 0,
    isDone: false,
    ...overrides,
  };
}
