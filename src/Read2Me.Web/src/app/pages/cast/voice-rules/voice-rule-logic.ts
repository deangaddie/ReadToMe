import { CreateVoiceRule, Guid, VoiceAnchorLevel, VoiceRuleDto } from '@app/api';

// ---- Rule list ---------------------------------------------------------------------------------------

/** The word Blazor puts before a node's title per level; a line's own text stands alone. */
const LEVEL_WORD: Record<VoiceAnchorLevel, string> = {
  Volume: 'Volume ',
  Part: 'Part ',
  Chapter: 'Chapter ',
  Paragraph: 'Paragraph ',
  ParagraphItem: '',
};

/** Blazor's node label: the level word, the node's title, or "(missing node)" when the anchor dangles. */
function nodeLabel(
  level: VoiceAnchorLevel | null,
  title: string | null,
  dangling: boolean,
): string {
  if (dangling || title === null) return '(missing node)';
  return `${level ? LEVEL_WORD[level] : ''}${title}`;
}

/**
 * The rule row's text, exactly as Blazor's `CharacterDetailPanel.RuleDescription` renders it:
 * "Default → Voice", "From X onward → Voice", "X → Voice" (one node), "X to Y → Voice".
 */
export function describeRule(rule: VoiceRuleDto): string {
  if (rule.isDefault) return `Default → ${rule.voiceName}`;

  const from = nodeLabel(rule.fromLevel, rule.fromTitle, rule.fromDangling);
  const to = nodeLabel(rule.toLevel, rule.toTitle, rule.toDangling);
  const fromHereOn = rule.toLevel === null && !rule.toDangling;
  const singleNode = rule.fromLevel === rule.toLevel && rule.fromNodeId === rule.toNodeId;

  if (fromHereOn) return `From ${from} onward → ${rule.voiceName}`;
  if (singleNode) return `${from} → ${rule.voiceName}`;
  return `${from} to ${to} → ${rule.voiceName}`;
}

/** Either anchor names a node that no longer exists; the evaluator skips the rule. */
export function isDangling(rule: VoiceRuleDto): boolean {
  return rule.fromDangling || rule.toDangling;
}

/**
 * Move/Delete show on non-default rules only, and never on the seed Narrator row while the narrator
 * link points at another character (Blazor's `IsLinkedNarrator`): that row's rules are inert then.
 */
export function showRuleControls(rule: VoiceRuleDto, seedNarratorLinked: boolean): boolean {
  return !rule.isDefault && !seedNarratorLinked;
}

export interface MoveAbility {
  up: boolean;
  down: boolean;
}

/** Up/Down among the non-default rules only, disabled at either end; the default rule never moves. */
export function moveAbility(rules: readonly VoiceRuleDto[], ruleId: Guid): MoveAbility {
  const movable = rules.filter((r) => !r.isDefault);
  const index = movable.findIndex((r) => r.ruleId === ruleId);
  if (index < 0) return { up: false, down: false };
  return { up: index > 0, down: index < movable.length - 1 };
}

// ---- Add rule dialog -------------------------------------------------------------------------------

export type RuleMode = 'fromHereOn' | 'justThisNode';

/** The cascading picker's five levels, shallowest first. */
export type AnchorPick = 'volume' | 'part' | 'chapter' | 'paragraph' | 'item';

export interface AnchorSelection {
  volumeId: Guid | null;
  partId: Guid | null;
  chapterId: Guid | null;
  paragraphId: Guid | null;
  itemId: Guid | null;
}

export const EMPTY_ANCHOR: AnchorSelection = {
  volumeId: null,
  partId: null,
  chapterId: null,
  paragraphId: null,
  itemId: null,
};

const PICK_ORDER: readonly AnchorPick[] = ['volume', 'part', 'chapter', 'paragraph', 'item'];
const PICK_KEY: Record<AnchorPick, keyof AnchorSelection> = {
  volume: 'volumeId',
  part: 'partId',
  chapter: 'chapterId',
  paragraph: 'paragraphId',
  item: 'itemId',
};
const PICK_LEVEL: Record<AnchorPick, VoiceAnchorLevel> = {
  volume: 'Volume',
  part: 'Part',
  chapter: 'Chapter',
  paragraph: 'Paragraph',
  item: 'ParagraphItem',
};

/**
 * Choosing (or clearing) a level resets every deeper level: a new chapter invalidates the
 * paragraph and line chosen under the old one. Shallower levels are kept.
 */
export function selectAnchor(
  selection: AnchorSelection,
  pick: AnchorPick,
  id: Guid | null,
): AnchorSelection {
  const next = { ...selection, [PICK_KEY[pick]]: id };
  for (const deeper of PICK_ORDER.slice(PICK_ORDER.indexOf(pick) + 1)) {
    next[PICK_KEY[deeper]] = null;
  }
  return next;
}

export interface Anchor {
  level: VoiceAnchorLevel;
  nodeId: Guid;
}

/** The deepest chosen level is the anchor; null when nothing is chosen. */
export function deepestAnchor(selection: AnchorSelection): Anchor | null {
  for (const pick of [...PICK_ORDER].reverse()) {
    const id = selection[PICK_KEY[pick]];
    if (id !== null) return { level: PICK_LEVEL[pick], nodeId: id };
  }
  return null;
}

/** Add is enabled once a voice and an anchor are both chosen. */
export function canSubmitRule(voiceId: Guid | null, selection: AnchorSelection): boolean {
  return voiceId !== null && deepestAnchor(selection) !== null;
}

/**
 * The command the dialog submits: "from here on" leaves the to end open, "just this node" closes
 * it on the same anchor (what Blazor's AddVoiceRuleDialog sends). Null while the dialog is invalid.
 */
export function ruleCommand(
  characterId: Guid,
  voiceId: Guid | null,
  mode: RuleMode,
  selection: AnchorSelection,
): CreateVoiceRule | null {
  const anchor = deepestAnchor(selection);
  if (voiceId === null || anchor === null) return null;
  const closed = mode === 'justThisNode';
  return {
    type: 'CreateVoiceRule',
    characterId,
    voiceId,
    fromLevel: anchor.level,
    fromNodeId: anchor.nodeId,
    toLevel: closed ? anchor.level : null,
    toNodeId: closed ? anchor.nodeId : null,
  };
}

/** A picker label for a paragraph or line: its first 40 characters, as Blazor truncates them. */
export function snippet(text: string | null | undefined, max = 40): string {
  const value = text ?? '';
  return value.length > max ? `${value.slice(0, max)}…` : value;
}
