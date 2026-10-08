import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import type { NodeStatusSummary } from '@app/live/live-messages';
import { countBadge, icon, statusChip } from '@app/ui/partials';
import { TypeAhead, typeAheadTarget } from '@app/ui/roving';
import type { TreeNode } from './book-tree';
import { NODE_MENU_OPEN, type NodeMenuRequest, nodeMenuTrigger } from './node-menu';
import type { ActionEntryId, NodeMenuTarget, SelectionKind } from './node-menu-entries';
import structureTreeCss from './structure-tree.css' with { type: 'text' };

adoptStyles(structureTreeCss);

/** One visible row of the tree: the node plus what its `aria-*` attributes say about it. */
export interface TreeRow {
  node: TreeNode;
  /** 0 for a root; `aria-level` is depth + 1. */
  depth: number;
  /** `aria-setsize`: the number of siblings, this node included. */
  setSize: number;
  /** `aria-posinset`: 1-based position among the siblings. */
  position: number;
  parent: TreeNode | null;
}

/** The rows a tree shows, in order: every root, then the children of each expanded node, recursively. */
export function flattenTree(nodes: readonly TreeNode[], expanded: ReadonlySet<string>): TreeRow[] {
  const rows: TreeRow[] = [];
  const walk = (siblings: readonly TreeNode[], depth: number, parent: TreeNode | null) => {
    siblings.forEach((node, index) => {
      rows.push({ node, depth, setSize: siblings.length, position: index + 1, parent });
      if (node.expandable && expanded.has(node.id)) walk(node.children, depth + 1, node);
    });
  };
  walk(nodes, 0, null);
  return rows;
}

/**
 * The reader's navigation (design §6.3): volumes / parts / chapters with roll-up badges from the
 * project's node status map. Expanding a node asks for its children; clicking a chapter asks the
 * reader to show it. Expansion is owned by the caller (`expandedIds`), so a re-created tree reopens
 * the nodes the reader left open. Each node carries a trigger for the shared `r2m-node-menu`
 * (ticket 11); a node whose attribution is queued or processing has it disabled.
 *
 * A flat ARIA tree written by hand (spec §2.3 point 8): one `role=treeitem` per visible row with
 * `aria-level`, `aria-setsize`, `aria-posinset` and `aria-expanded`, a roving tabindex, and the CDK
 * `TreeKeyManager` keys — arrows, Home/End, Enter/Space (a chapter opens; a parent is toggled by
 * its chevron or Left/Right, as in Angular), `*`, type-ahead over the titles, and Shift+F10 /
 * ContextMenu for the node menu, since the trigger inside a row is not a tab stop. The
 * selection's checkboxes and its menu shortcuts (tickets 12 and 13) arrive with native-web 25;
 * the tree already hands a chosen shortcut on as `node-action`.
 *
 * Events: `expanded-change` ({ node, expanded }), `select-chapter` (chapter id),
 * `node-action` ({ node, action }).
 */
export class StructureTree extends R2mElement {
  #nodes = signal<readonly TreeNode[]>([]);
  get nodes() {
    return this.#nodes();
  }
  set nodes(value: readonly TreeNode[]) {
    this.#nodes.set(value);
  }

  #statuses = signal<Readonly<Record<string, NodeStatusSummary | null | undefined>>>({});
  get statuses() {
    return this.#statuses();
  }
  set statuses(value: Readonly<Record<string, NodeStatusSummary | null | undefined>>) {
    this.#statuses.set(value);
  }

  #currentChapterId = signal<string | null>(null);
  get currentChapterId() {
    return this.#currentChapterId();
  }
  set currentChapterId(value: string | null) {
    this.#currentChapterId.set(value);
  }

  /** Ids of the nodes to show expanded. */
  #expandedIds = signal<ReadonlySet<string>>(new Set());
  get expandedIds() {
    return this.#expandedIds();
  }
  set expandedIds(value: ReadonlySet<string>) {
    this.#expandedIds.set(value);
  }

  /** Which selection is on (that selection's menu entries); null for none. */
  #selection = signal<SelectionKind | null>(null);
  get selection() {
    return this.#selection();
  }
  set selection(value: SelectionKind | null) {
    this.#selection.set(value);
  }

  /** The editor is locked (stale view or a write in flight): every node menu is off. */
  #locked = signal(false);
  get locked() {
    return this.#locked();
  }
  set locked(value: boolean) {
    this.#locked.set(value);
  }

  /** The row holding the roving tab stop; null until the user moves it (the current chapter then). */
  readonly #active = signal<string | null>(null);
  readonly #rows = computed(() => flattenTree(this.#nodes(), this.#expandedIds()));
  /** The tab stop: the row last focused, else the current chapter, else the first row. */
  readonly #tabStop = computed(() => {
    const rows = this.#rows();
    const has = (id: string | null) => id !== null && rows.some((r) => r.node.id === id);
    const active = this.#active();
    if (has(active)) return active;
    const current = this.#currentChapterId();
    return has(current) ? current : (rows[0]?.node.id ?? null);
  });
  readonly #typeAhead = new TypeAhead((query) => this.#jumpTo(query));

  protected override connected(): void {
    this.addEventListener('keydown', this.#onKeydown);
    this.addEventListener('focusin', this.#onFocusIn);
    this.addEventListener(NODE_MENU_OPEN, this.#onMenuOpen);
    this.onDisconnect(() => {
      this.removeEventListener('keydown', this.#onKeydown);
      this.removeEventListener('focusin', this.#onFocusIn);
      this.removeEventListener(NODE_MENU_OPEN, this.#onMenuOpen);
      this.#typeAhead.cancel();
    });
  }

  protected template() {
    const rows = this.#rows();
    const tabStop = this.#tabStop();
    const current = this.#currentChapterId();
    const expanded = this.#expandedIds();
    const statuses = this.#statuses();
    const selection = this.#selection();
    const locked = this.#locked();
    return html`<div class="tree" role="tree" aria-label="Book structure">
      ${repeat(
        rows,
        (row) => row.node.id,
        (row) => {
          const { node } = row;
          const isOpen = node.expandable && expanded.has(node.id);
          const isCurrent = node.id === current;
          const status = statuses[node.id] ?? null;
          const busy = !!status && (status.attributionProcessing || status.attributionQueued > 0);
          return html`<div
            role="treeitem"
            class="tree__node ${isCurrent ? 'tree__node--current' : ''}"
            tabindex=${node.id === tabStop ? '0' : '-1'}
            aria-level=${row.depth + 1}
            aria-setsize=${row.setSize}
            aria-posinset=${row.position}
            aria-expanded=${node.expandable ? (isOpen ? 'true' : 'false') : nothing}
            aria-current=${isCurrent ? 'true' : nothing}
            data-node-id=${node.id}
            style="--tree-depth: ${row.depth}"
            @click=${() => this.#onNodeClick(node)}
          >
            ${
              node.expandable
                ? html`<button
                    type="button"
                    class="tree__toggle"
                    tabindex="-1"
                    aria-label="Toggle ${node.title}"
                    @click=${(e: Event) => {
                      e.stopPropagation();
                      this.#focusRow(node.id);
                      this.#setExpanded(node, !isOpen);
                    }}
                  >
                    ${icon(isOpen ? 'expand_more' : 'chevron_right')}
                  </button>`
                : html`<span class="tree__toggle" aria-hidden="true"></span>`
            }
            <span class="tree__title">${node.title}</span>
            ${
              isOpen && !node.loaded
                ? html`<span class="tree__loading" aria-busy="true" aria-label="Loading"></span>`
                : nothing
            }
            ${status ? badges(status) : nothing}
            <span class="tree__menu">
              ${nodeMenuTrigger(targetOf(node, selection), { disabled: busy || locked, tabIndex: -1 })}
            </span>
          </div>`;
        },
      )}
    </div>`;
  }

  // ---- pointer ----------------------------------------------------------------------------------

  /** A click focuses the row and, on a chapter, opens it; a parent's chevron is its toggle. */
  #onNodeClick(node: TreeNode): void {
    this.#focusRow(node.id);
    if (node.level === 'chapter') this.emit('select-chapter', node.id);
  }

  /** Focus that arrived by any route (a click, a tab, a script) makes that row the tab stop. */

  // ---- keys (CDK TreeKeyManager) ----------------------------------------------------------------

  readonly #onKeydown = (event: KeyboardEvent): void => {
    const target = event.target as HTMLElement;
    // Keys from a control inside a row (the menu trigger) are that control's own.
    if (target.getAttribute('role') !== 'treeitem') return;
    const rows = this.#rows();
    const index = rows.findIndex((r) => r.node.id === target.dataset['nodeId']);
    const row = rows[index];
    if (!row) return;
    const { node } = row;

    const move = (to: number) => {
      event.preventDefault();
      const next = rows[Math.max(0, Math.min(rows.length - 1, to))];
      if (next) this.#focusRow(next.node.id);
    };

    switch (event.key) {
      case 'ArrowDown':
        return move(index + 1);
      case 'ArrowUp':
        return move(index - 1);
      case 'Home':
        return move(0);
      case 'End':
        return move(rows.length - 1);
      case 'ArrowRight': {
        event.preventDefault();
        if (!node.expandable) return;
        if (!this.#expandedIds().has(node.id)) return this.#setExpanded(node, true);
        const [first] = node.children;
        if (first && node.loaded) this.#focusRow(first.id);
        return;
      }
      case 'ArrowLeft': {
        event.preventDefault();
        if (node.expandable && this.#expandedIds().has(node.id)) {
          return this.#setExpanded(node, false);
        }
        if (row.parent) this.#focusRow(row.parent.id);
        return;
      }
      case 'Enter':
      case ' ':
        event.preventDefault();
        if (node.level === 'chapter') this.emit('select-chapter', node.id);
        return;
      case '*': {
        event.preventDefault();
        const siblings = row.parent ? row.parent.children : this.#nodes();
        for (const sibling of siblings) {
          if (sibling.expandable && !this.#expandedIds().has(sibling.id)) {
            this.#setExpanded(sibling, true);
          }
        }
        return;
      }
      case 'ContextMenu':
      case 'F10':
        if (event.key === 'F10' && !event.shiftKey) return;
        event.preventDefault();
        target.querySelector<HTMLElement>('.r2m-node-menu__trigger')?.click();
        return;
      default:
        this.#typeAhead.keydown(event);
    }
  };

  #jumpTo(query: string): void {
    const rows = this.#rows();
    const current = rows.findIndex((r) => r.node.id === this.#tabStop());
    const index = typeAheadTarget(
      query,
      rows.map((r) => r.node.title),
      current,
    );
    if (index !== null) this.#focusRow(rows[index]!.node.id);
  }

  readonly #onFocusIn = (event: FocusEvent): void => {
    const id = (event.target as HTMLElement).closest<HTMLElement>('[role="treeitem"]')?.dataset[
      'nodeId'
    ];
    if (id !== undefined) this.#active.set(id);
  };

  /** Moves the tab stop and focus to a row (the roving tabindex). */
  #focusRow(id: string): void {
    this.#active.set(id);
    const item = this.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(id)}"]`);
    if (!item) return;
    for (const other of this.querySelectorAll<HTMLElement>('[role="treeitem"]')) {
      other.tabIndex = other === item ? 0 : -1;
    }
    item.focus();
  }

  #setExpanded(node: TreeNode, expanded: boolean): void {
    this.emit('expanded-change', { node, expanded });
  }

  // ---- node menu --------------------------------------------------------------------------------

  /** The open request passes through on its way to the shared menu: name the row and take the actions. */
  readonly #onMenuOpen = (event: Event): void => {
    const request = (event as CustomEvent<NodeMenuRequest>).detail;
    const item = request.anchor.closest<HTMLElement>('[role="treeitem"]');
    const node = this.#rows().find((r) => r.node.id === item?.dataset['nodeId'])?.node;
    if (!item || !node) return;
    request.returnTo = item;
    request.onAction = (action: ActionEntryId) => this.emit('node-action', { node, action });
  };
}

function targetOf(node: TreeNode, selection: SelectionKind | null): NodeMenuTarget {
  const target: NodeMenuTarget = {
    kind: node.level,
    id: node.id,
    text: node.rawTitle,
    isFirst: node.isFirst,
    isLast: node.isLast,
  };
  // (A TreeNode is not a RowPosition, so the two flags are named rather than spread.)
  return selection ? { ...target, selection } : target;
}

function badges(s: NodeStatusSummary) {
  return html`<span class="tree__badges">
    ${
      s.attributionProcessing
        ? statusChip({ status: 'busy', label: 'Processing', compact: true })
        : s.attributionQueued > 0
          ? statusChip({
              status: 'info',
              icon: 'schedule',
              label: `${s.attributionQueued} queued`,
              compact: true,
            })
          : nothing
    }
    ${countBadge('attribution', s.attributionRemaining, 'Paragraphs needing attribution')}
    ${countBadge('audio', s.audioRemaining, 'Paragraphs needing audio')}
    ${countBadge('review', s.review, 'Paragraphs with audio to review')}
    ${
      s.isDone
        ? html`<span
            class="material-symbols-rounded r2m-icon tree__done"
            aria-label="Done"
            >check_circle</span
          >`
        : nothing
    }
  </span>`;
}

define('r2m-structure-tree', StructureTree);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-structure-tree': StructureTree;
  }
}
