import { CdkTree, CdkTreeModule } from '@angular/cdk/tree';
import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { NodeStatusSummary } from '@app/live/live-messages';
import { CountBadge } from '@app/ui/count-badge/count-badge';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { TreeNode } from './book-tree';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';

/**
 * The reader's navigation (design §6.3): volumes / parts / chapters with roll-up badges from the
 * project's node status map. Expanding a node asks for its children; clicking a chapter asks the
 * reader to show it. Expansion is owned by the caller (`expandedIds`), so a re-created tree reopens
 * the nodes the reader left open. Each node carries its menu (ticket 11); a node whose
 * attribution is queued or processing has it disabled.
 */
@Component({
  selector: 'app-structure-tree',
  imports: [CdkTreeModule, MatIconModule, CountBadge, StatusChip, NodeMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cdk-tree
      class="tree"
      [dataSource]="nodes()"
      [childrenAccessor]="childrenOf"
      [expansionKey]="keyOf"
      aria-label="Book structure"
    >
      <cdk-tree-node
        *cdkTreeNodeDef="let node"
        cdkTreeNodePadding
        [cdkTreeNodePaddingIndent]="14"
        [isExpandable]="node.expandable"
        class="tree__node"
        [class.tree__node--current]="node.id === currentChapterId()"
        [attr.data-node-id]="node.id"
        (expandedChange)="onExpanded(node, $event)"
        (activation)="onActivate(node)"
      >
        @if (node.expandable) {
          <button
            type="button"
            class="tree__toggle"
            cdkTreeNodeToggle
            tabindex="-1"
            [attr.aria-label]="'Toggle ' + node.title"
          >
            <mat-icon aria-hidden="true">{{
              expandedIds().has(node.id) ? 'expand_more' : 'chevron_right'
            }}</mat-icon>
          </button>
        } @else {
          <span class="tree__toggle" aria-hidden="true"></span>
        }

        <button
          type="button"
          class="tree__title"
          tabindex="-1"
          [attr.aria-current]="node.id === currentChapterId() ? 'true' : null"
          (click)="onActivate(node)"
        >
          {{ node.title }}
        </button>

        @if (node.expandable && expandedIds().has(node.id) && !node.loaded) {
          <span class="tree__loading" aria-busy="true" aria-label="Loading"></span>
        }

        @if (statuses()[node.id]; as s) {
          <span class="tree__badges">
            @if (s.attributionProcessing) {
              <r2m-status-chip compact status="busy" label="Processing" />
            } @else if (s.attributionQueued > 0) {
              <r2m-status-chip
                compact
                status="info"
                icon="schedule"
                [label]="s.attributionQueued + ' queued'"
              />
            }
            <r2m-count-badge
              kind="attribution"
              [count]="s.attributionRemaining"
              title="Paragraphs needing attribution"
            />
            <r2m-count-badge
              kind="audio"
              [count]="s.audioRemaining"
              title="Paragraphs needing audio"
            />
            <r2m-count-badge
              kind="review"
              [count]="s.review"
              title="Paragraphs with audio to review"
            />
            @if (s.isDone) {
              <mat-icon class="tree__done" aria-label="Done">check_circle</mat-icon>
            }
          </span>
        }

        <r2m-node-menu
          class="tree__menu"
          [target]="targetOf(node)"
          [disabled]="isBusy(node)"
          (click)="$event.stopPropagation()"
        />
      </cdk-tree-node>
    </cdk-tree>
  `,
  styles: `
    :host {
      display: block;
      font-size: var(--r2m-text-md);
    }
    .tree__node {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      min-height: 32px;
      padding-right: var(--r2m-space-2);
      border-radius: var(--r2m-radius-sm);
      outline: none;
    }
    .tree__node:focus-visible {
      box-shadow: inset 0 0 0 2px var(--r2m-accent);
    }
    .tree__node--current {
      background: var(--r2m-nav-active-bg);
      color: var(--r2m-nav-active-fg);
    }
    .tree__toggle {
      flex: 0 0 24px;
      width: 24px;
      height: 24px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0;
      border: none;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .tree__title {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      text-align: left;
      padding: 0;
      border: none;
      background: transparent;
      color: inherit;
      font: inherit;
      cursor: pointer;
    }
    .tree__badges {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      flex: 0 0 auto;
    }
    .tree__loading {
      flex: 0 0 auto;
      width: var(--r2m-space-8);
      height: var(--r2m-space-2);
      border-radius: var(--r2m-radius-pill);
      background: color-mix(in srgb, var(--r2m-text-muted) 20%, transparent);
    }
    .tree__done {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--r2m-status-ok);
    }
    .tree__menu {
      flex: 0 0 auto;
      opacity: 0;
      transition: opacity 120ms;
    }
    .tree__node:hover .tree__menu,
    .tree__node:focus-within .tree__menu,
    .tree__node--current .tree__menu {
      opacity: 1;
    }
  `,
})
export class StructureTree {
  readonly nodes = input.required<TreeNode[]>();
  readonly statuses = input<Readonly<Record<string, NodeStatusSummary | null>>>({});
  readonly currentChapterId = input<string | null>(null);

  /** Ids of the nodes to show expanded. */
  readonly expandedIds = input<ReadonlySet<string>>(new Set());

  /** The user expanded or collapsed a node. */
  readonly expandedChange = output<{ node: TreeNode; expanded: boolean }>();
  readonly selectChapter = output<string>();

  private readonly tree = viewChild(CdkTree<TreeNode, string>);
  private readonly injector = inject(Injector);

  protected readonly childrenOf = (node: TreeNode) => node.children;
  protected readonly keyOf = (node: TreeNode) => node.id;

  constructor() {
    // The CDK tree's own expansion model starts empty; open what the caller says is expanded.
    effect(() => {
      const nodes = this.nodes();
      const ids = this.expandedIds();
      untracked(() =>
        afterNextRender(() => this.restoreExpansion(nodes, ids), { injector: this.injector }),
      );
    });
  }

  protected onExpanded(node: TreeNode, expanded: boolean): void {
    this.expandedChange.emit({ node, expanded });
  }

  private restoreExpansion(nodes: readonly TreeNode[], ids: ReadonlySet<string>): void {
    const tree = this.tree();
    if (!tree) return;
    for (const node of nodes) {
      if (!ids.has(node.id)) continue;
      if (!tree.isExpanded(node)) tree.expand(node);
      this.restoreExpansion(node.children, ids);
    }
  }

  protected onActivate(node: TreeNode): void {
    if (node.level === 'chapter') this.selectChapter.emit(node.id);
  }

  protected targetOf(node: TreeNode): NodeMenuTarget {
    return {
      kind: node.level,
      id: node.id,
      text: node.rawTitle,
      isFirst: node.isFirst,
      isLast: node.isLast,
    };
  }

  /** A node with attribution queued or running under it: its structure is about to change. */
  protected isBusy(node: TreeNode): boolean {
    const s = this.statuses()[node.id];
    return !!s && (s.attributionProcessing || s.attributionQueued > 0);
  }
}
