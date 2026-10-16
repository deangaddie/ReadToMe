import { describe, expect, it } from 'bun:test';
import type { NodeStatusSummary } from '@app/live/live-messages';
import { type StructureNodeMenuInput, structureNodeMenu } from './structure-node-menu';

const middle = { isFirst: false, isLast: false };

function input(overrides: Partial<StructureNodeMenuInput> = {}): StructureNodeMenuInput {
  return {
    node: { level: 'chapter', id: 'c1', rawTitle: 'The Storm' },
    position: middle,
    selection: null,
    status: null,
    locked: false,
    ...overrides,
  };
}

describe('structureNodeMenu', () => {
  it('targets the node by its level and id, carrying the stored title', () => {
    const { target, disabled } = structureNodeMenu(input());
    expect(target).toEqual({
      kind: 'chapter',
      id: 'c1',
      text: 'The Storm',
      isFirst: false,
      isLast: false,
    });
    expect(disabled).toBe(false);
  });

  it('an untitled node carries a null title, not its placeholder', () => {
    const { target } = structureNodeMenu(
      input({ node: { level: 'volume', id: 'v2', rawTitle: null } }),
    );
    expect(target).toMatchObject({ kind: 'volume', id: 'v2', text: null });
  });

  it.each([
    [{ isFirst: true, isLast: false }],
    [{ isFirst: false, isLast: true }],
    [{ isFirst: true, isLast: true }],
  ])('passes the sibling position through for the merge entries: %o', (position) => {
    const { target } = structureNodeMenu(
      input({ node: { level: 'part', id: 'p1', rawTitle: 'One' }, position }),
    );
    expect(target).toMatchObject(position);
  });

  it('adds the selection kind when the reader is selecting', () => {
    expect(structureNodeMenu(input({ selection: 'paragraphs' })).target.selection).toBe(
      'paragraphs',
    );
    expect(structureNodeMenu(input({ selection: 'items' })).target.selection).toBe('items');
  });

  it('leaves the selection kind out when nothing is selectable', () => {
    expect('selection' in structureNodeMenu(input()).target).toBe(false);
  });

  it('disables the trigger while the node is processing attribution', () => {
    const menu = structureNodeMenu(input({ status: status({ attributionProcessing: true }) }));
    expect(menu.disabled).toBe(true);
  });

  it('disables the trigger while the node has paragraphs queued for attribution', () => {
    const menu = structureNodeMenu(input({ status: status({ attributionQueued: 2 }) }));
    expect(menu.disabled).toBe(true);
  });

  it('keeps the trigger on for an idle node, whatever else it still needs', () => {
    const menu = structureNodeMenu(
      input({
        status: status({
          attributionRemaining: 4,
          audioRemaining: 3,
          review: 1,
        }),
      }),
    );
    expect(menu.disabled).toBe(false);
  });

  it('disables the trigger while the editor is locked', () => {
    expect(structureNodeMenu(input({ locked: true })).disabled).toBe(true);
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
