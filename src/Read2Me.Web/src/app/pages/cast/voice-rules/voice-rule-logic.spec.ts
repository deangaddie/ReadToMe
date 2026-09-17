import { VoiceRuleDto } from '@app/api';
import {
  EMPTY_ANCHOR,
  canSubmitRule,
  deepestAnchor,
  describeRule,
  isDangling,
  moveAbility,
  ruleCommand,
  selectAnchor,
  showRuleControls,
} from './voice-rule-logic';

function rule(overrides: Partial<VoiceRuleDto> = {}): VoiceRuleDto {
  return {
    ruleId: 'r1',
    voiceId: 'v1',
    voiceName: 'Voice B',
    isDefault: false,
    fromLevel: null,
    fromNodeId: null,
    fromTitle: null,
    fromDangling: false,
    toLevel: null,
    toNodeId: null,
    toTitle: null,
    toDangling: false,
    order: 'a1',
    ...overrides,
  };
}

describe('describeRule (Blazor CharacterDetailPanel.RuleDescription parity)', () => {
  it('default rule', () => {
    expect(describeRule(rule({ isDefault: true, voiceName: 'Narrator' }))).toBe(
      'Default → Narrator',
    );
  });

  it('from here on at chapter level', () => {
    expect(
      describeRule(rule({ fromLevel: 'Chapter', fromNodeId: 'c5', fromTitle: 'Chapter 5' })),
    ).toBe('From Chapter Chapter 5 onward → Voice B');
  });

  it('just this node at chapter level', () => {
    expect(
      describeRule(
        rule({
          fromLevel: 'Chapter',
          fromNodeId: 'c3',
          fromTitle: 'Chapter 3',
          toLevel: 'Chapter',
          toNodeId: 'c3',
          toTitle: 'Chapter 3',
        }),
      ),
    ).toBe('Chapter Chapter 3 → Voice B');
  });

  it('span between two nodes', () => {
    expect(
      describeRule(
        rule({
          fromLevel: 'Part',
          fromNodeId: 'p1',
          fromTitle: 'One',
          toLevel: 'Part',
          toNodeId: 'p2',
          toTitle: 'Two',
        }),
      ),
    ).toBe('Part One to Part Two → Voice B');
  });

  it('a line anchor carries no level prefix', () => {
    expect(
      describeRule(
        rule({
          fromLevel: 'ParagraphItem',
          fromNodeId: 'i1',
          fromTitle: '“Hello there,” she said.',
          toLevel: 'ParagraphItem',
          toNodeId: 'i1',
          toTitle: '“Hello there,” she said.',
        }),
      ),
    ).toBe('“Hello there,” she said. → Voice B');
  });

  it('dangling anchors read as missing node', () => {
    expect(
      describeRule(rule({ fromLevel: 'Chapter', fromNodeId: 'gone', fromDangling: true })),
    ).toBe('From (missing node) onward → Voice B');
    expect(
      describeRule(
        rule({
          fromLevel: 'Chapter',
          fromNodeId: 'gone',
          fromDangling: true,
          toLevel: 'Chapter',
          toNodeId: 'gone',
          toDangling: true,
        }),
      ),
    ).toBe('(missing node) → Voice B');
  });

  it('a from-here-on rule whose to end dangles reads as a span', () => {
    expect(
      describeRule(
        rule({
          fromLevel: 'Chapter',
          fromNodeId: 'c1',
          fromTitle: 'One',
          toLevel: 'Chapter',
          toNodeId: 'gone',
          toDangling: true,
        }),
      ),
    ).toBe('Chapter One to (missing node) → Voice B');
  });
});

describe('isDangling', () => {
  it('is true when either anchor dangles', () => {
    expect(isDangling(rule())).toBe(false);
    expect(isDangling(rule({ fromDangling: true }))).toBe(true);
    expect(isDangling(rule({ toDangling: true }))).toBe(true);
  });
});

describe('showRuleControls', () => {
  it('hides move/delete on the default rule and on the seed Narrator while linked elsewhere', () => {
    expect(showRuleControls(rule(), false)).toBe(true);
    expect(showRuleControls(rule({ isDefault: true }), false)).toBe(false);
    expect(showRuleControls(rule(), true)).toBe(false);
  });
});

describe('moveAbility', () => {
  const rules = [
    rule({ ruleId: 'd', isDefault: true, order: 'a0' }),
    rule({ ruleId: 'r1', order: 'a1' }),
    rule({ ruleId: 'r2', order: 'a2' }),
    rule({ ruleId: 'r3', order: 'a3' }),
  ];

  it('disables up at the first non-default rule and down at the last', () => {
    expect(moveAbility(rules, 'r1')).toEqual({ up: false, down: true });
    expect(moveAbility(rules, 'r2')).toEqual({ up: true, down: true });
    expect(moveAbility(rules, 'r3')).toEqual({ up: true, down: false });
  });

  it('a lone non-default rule can move neither way', () => {
    expect(moveAbility(rules.slice(0, 2), 'r1')).toEqual({ up: false, down: false });
  });

  it('the default rule never moves', () => {
    expect(moveAbility(rules, 'd')).toEqual({ up: false, down: false });
  });
});

describe('selectAnchor (cascading reset)', () => {
  const full = {
    volumeId: 'v',
    partId: 'p',
    chapterId: 'c',
    paragraphId: 'q',
    itemId: 'i',
  };

  it('choosing a level clears every deeper level and keeps shallower ones', () => {
    expect(selectAnchor(full, 'chapter', 'c2')).toEqual({
      volumeId: 'v',
      partId: 'p',
      chapterId: 'c2',
      paragraphId: null,
      itemId: null,
    });
    expect(selectAnchor(full, 'volume', 'v2')).toEqual({
      ...EMPTY_ANCHOR,
      volumeId: 'v2',
    });
  });

  it('clearing a level clears it and everything deeper', () => {
    expect(selectAnchor(full, 'part', null)).toEqual({
      ...EMPTY_ANCHOR,
      volumeId: 'v',
    });
    expect(selectAnchor(full, 'item', null)).toEqual({ ...full, itemId: null });
  });
});

describe('deepestAnchor / canSubmitRule / ruleCommand', () => {
  it('the deepest chosen level becomes the anchor', () => {
    expect(deepestAnchor(EMPTY_ANCHOR)).toBeNull();
    expect(deepestAnchor({ ...EMPTY_ANCHOR, volumeId: 'v' })).toEqual({
      level: 'Volume',
      nodeId: 'v',
    });
    expect(deepestAnchor({ ...EMPTY_ANCHOR, volumeId: 'v', partId: 'p', chapterId: 'c' })).toEqual({
      level: 'Chapter',
      nodeId: 'c',
    });
    expect(
      deepestAnchor({ volumeId: 'v', partId: 'p', chapterId: 'c', paragraphId: 'q', itemId: 'i' }),
    ).toEqual({ level: 'ParagraphItem', nodeId: 'i' });
  });

  it('submit needs a voice and an anchor', () => {
    expect(canSubmitRule(null, { ...EMPTY_ANCHOR, volumeId: 'v' })).toBe(false);
    expect(canSubmitRule('voice', EMPTY_ANCHOR)).toBe(false);
    expect(canSubmitRule('voice', { ...EMPTY_ANCHOR, volumeId: 'v' })).toBe(true);
  });

  it('from here on leaves the to end open; just this node closes it on the same anchor', () => {
    const anchor = { ...EMPTY_ANCHOR, volumeId: 'v', partId: 'p', chapterId: 'c' };
    expect(ruleCommand('alice', 'voice', 'fromHereOn', anchor)).toEqual({
      type: 'CreateVoiceRule',
      characterId: 'alice',
      voiceId: 'voice',
      fromLevel: 'Chapter',
      fromNodeId: 'c',
      toLevel: null,
      toNodeId: null,
    });
    expect(ruleCommand('alice', 'voice', 'justThisNode', anchor)).toEqual({
      type: 'CreateVoiceRule',
      characterId: 'alice',
      voiceId: 'voice',
      fromLevel: 'Chapter',
      fromNodeId: 'c',
      toLevel: 'Chapter',
      toNodeId: 'c',
    });
    expect(ruleCommand('alice', 'voice', 'fromHereOn', EMPTY_ANCHOR)).toBeNull();
    expect(ruleCommand('alice', null, 'fromHereOn', anchor)).toBeNull();
  });
});
