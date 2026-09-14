import { ManualImportRequest, SplitRuleMode, SplitRuleRequest } from '@app/api';

/**
 * The manual reread dialog's form (research §3 ManualRereadDialog), validated the way the host and
 * the Blazor form validate it: a level that is switched on needs a rule, and a prefix rule needs a
 * prefix. Pure so the dialog stays a thin view over it.
 */

export interface LevelRule {
  mode: SplitRuleMode;
  prefix: string;
}

export interface ManualRereadForm {
  hasVolumes: boolean;
  hasParts: boolean;
  volume: LevelRule;
  part: LevelRule;
  chapter: LevelRule;
}

export type LevelKey = 'volume' | 'part' | 'chapter';

export const SPLIT_MODES: readonly { mode: SplitRuleMode; label: string }[] = [
  { mode: 'Prefix', label: 'Text prefix' },
  { mode: 'Arabic', label: 'Number (Arabic)' },
  { mode: 'Roman', label: 'Roman numerals' },
];

export const DEFAULT_MANUAL_REREAD_FORM: ManualRereadForm = {
  hasVolumes: false,
  hasParts: false,
  volume: { mode: 'Prefix', prefix: '' },
  part: { mode: 'Prefix', prefix: '' },
  chapter: { mode: 'Prefix', prefix: '' },
};

const LEVEL_WORD: Record<LevelKey, string> = { volume: 'Volume', part: 'Part', chapter: 'Chapter' };

/** The levels the form currently asks about, in book order. */
export function activeLevels(form: ManualRereadForm): LevelKey[] {
  const levels: LevelKey[] = [];
  if (form.hasVolumes) levels.push('volume');
  if (form.hasParts) levels.push('part');
  levels.push('chapter');
  return levels;
}

/** The first problem, worded as the Blazor form words it; null when the form can be sent. */
export function validateManualReread(form: ManualRereadForm): string | null {
  for (const level of activeLevels(form)) {
    const rule = form[level];
    if (rule.mode === 'Prefix' && !rule.prefix.trim()) {
      return `${LEVEL_WORD[level]} prefix cannot be empty.`;
    }
  }
  return null;
}

export function toManualImportRequest(form: ManualRereadForm): ManualImportRequest {
  return {
    hasMultipleVolumes: form.hasVolumes,
    hasMultipleParts: form.hasParts,
    volume: form.hasVolumes ? toRule(form.volume) : null,
    part: form.hasParts ? toRule(form.part) : null,
    chapter: toRule(form.chapter),
  };
}

function toRule(rule: LevelRule): SplitRuleRequest {
  return rule.mode === 'Prefix'
    ? { mode: 'Prefix', prefix: rule.prefix.trim() }
    : { mode: rule.mode, prefix: null };
}
