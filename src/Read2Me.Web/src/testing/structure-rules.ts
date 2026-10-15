import type { NodeKind, Placement, StructurePlacementDto, StructureRuleDto } from '@app/api';
import type { BookStore } from '@app/pages/book/book-store';

/**
 * A fixture copy of the host's Structure rules (`StructureRules.cs`), as the book overview sends
 * them. The node menu renders from whatever table it is given, so a spec that needs another shape
 * builds one with {@link rule}; this is only the usual book.
 */
export function rule(
  anchor: StructureRuleDto['anchor'],
  nodeKind: NodeKind,
  placements: readonly (readonly [Placement, NodeKind, boolean])[],
  minimumCount = 0,
): StructureRuleDto {
  return {
    anchor,
    nodeKind,
    placements: placements.map(([placement, creates, textRequired]): StructurePlacementDto => ({
      placement,
      creates,
      textRequired,
    })),
    minimumCount,
  };
}

export const STRUCTURE_RULES: StructureRuleDto[] = [
  rule(
    'Volume',
    'Volume',
    [
      ['Before', 'Volume', false],
      ['After', 'Volume', false],
      ['AtStart', 'Part', false],
      ['AtEnd', 'Part', false],
    ],
    1,
  ),
  rule('Part', 'Part', [
    ['Before', 'Part', false],
    ['After', 'Part', false],
    ['AtStart', 'Chapter', false],
    ['AtEnd', 'Chapter', false],
  ]),
  rule('Chapter', 'Chapter', [
    ['Before', 'Chapter', false],
    ['After', 'Chapter', false],
    ['AtStart', 'Paragraph', true],
    ['AtEnd', 'Paragraph', true],
  ]),
  rule('Paragraph', 'Paragraph', [
    ['Before', 'Paragraph', true],
    ['After', 'Paragraph', true],
    ['AtStart', 'ParagraphItem', true],
    ['AtEnd', 'ParagraphItem', true],
  ]),
  rule('PauseParagraph', 'Paragraph', [
    ['Before', 'Paragraph', true],
    ['After', 'Paragraph', true],
  ]),
  rule('ParagraphItem', 'ParagraphItem', [
    ['Before', 'ParagraphItem', true],
    ['After', 'ParagraphItem', true],
  ]),
];

/**
 * The slice of `BookStore` the node menu reads — the overview's Structure rules and volume list —
 * for a spec that mounts the menu or runs its entries without loading a book.
 */
export function menuBookStore(
  volumeCount = 2,
  rules: StructureRuleDto[] = STRUCTURE_RULES,
): BookStore {
  const volumes = Array.from({ length: volumeCount }, (_, i) => ({ id: `v${i + 1}`, title: null }));
  return { overview: () => ({ structureRules: rules, volumes }) } as unknown as BookStore;
}
