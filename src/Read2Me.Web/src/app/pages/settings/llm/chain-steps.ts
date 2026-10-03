import { AttributionChainStep, AttributionPromptStyle, LlmServerConfig } from '@app/api';

/**
 * Pure edits over the stored attribution chain. Steps
 * are addressed by index, not config id: one config may hold several rungs differing only in
 * thinking and prompt style. Both flags are fixed at add time.
 */

/** One rendered rung; `index` addresses the stored step, which orphans make sparse. */
export interface ChainRow {
  index: number;
  config: LlmServerConfig;
  thinking: boolean;
  /** The style the rung runs as: its own, else its config's. */
  style: AttributionPromptStyle;
}

/** One addable rung: a config in one thinking × style variant. */
export interface ChainOption {
  config: LlmServerConfig;
  thinking: boolean;
  promptStyle: AttributionPromptStyle;
}

export function moveStep(
  steps: AttributionChainStep[],
  index: number,
  delta: -1 | 1,
): AttributionChainStep[] {
  const target = index + delta;
  if (index < 0 || index >= steps.length || target < 0 || target >= steps.length) return steps;
  return steps.map((step, i) =>
    i === index ? steps[target]! : i === target ? steps[index]! : step,
  );
}

export function removeStep(steps: AttributionChainStep[], index: number): AttributionChainStep[] {
  return index < 0 || index >= steps.length ? steps : steps.filter((_, i) => i !== index);
}

export function addStep(
  steps: AttributionChainStep[],
  step: AttributionChainStep,
): AttributionChainStep[] {
  const present = steps.some(
    (s) =>
      s.configId === step.configId &&
      s.thinking === step.thinking &&
      s.promptStyle === step.promptStyle,
  );
  return present ? steps : [...steps, step];
}

const effectiveStyle = (step: AttributionChainStep, config: LlmServerConfig) =>
  step.promptStyle ?? config.promptStyle;

export function chainRows(
  steps: readonly AttributionChainStep[],
  configs: readonly LlmServerConfig[],
): ChainRow[] {
  const byId = new Map(configs.map((c) => [c.id, c]));
  return steps.flatMap((step, index) => {
    const config = byId.get(step.configId);
    if (!config) return [];
    return [{ index, config, thinking: step.thinking, style: effectiveStyle(step, config) }];
  });
}

/**
 * The style × thinking variants a rung can be added as. Chapter is fast only: the chapter pass
 * always sends thinking off, so a thinking Chapter rung would run exactly like the fast one.
 */
const VARIANTS: readonly { promptStyle: AttributionPromptStyle; thinking: boolean }[] = [
  { promptStyle: AttributionPromptStyle.Full, thinking: false },
  { promptStyle: AttributionPromptStyle.Simple, thinking: false },
  { promptStyle: AttributionPromptStyle.Chapter, thinking: false },
  { promptStyle: AttributionPromptStyle.Full, thinking: true },
  { promptStyle: AttributionPromptStyle.Simple, thinking: true },
];

/**
 * Every config in five variants (full/simple × fast/thinking, plus chapter fast) minus the ones
 * already in the chain. Compared on the effective style, so a legacy step with no stored style
 * occupies the variant it actually runs as.
 */
export function chainOptions(
  steps: readonly AttributionChainStep[],
  configs: readonly LlmServerConfig[],
): ChainOption[] {
  const key = (id: number, thinking: boolean, style: AttributionPromptStyle) =>
    `${id}:${thinking}:${style}`;
  const present = new Set(
    chainRows(steps, configs).map((r) => key(r.config.id, r.thinking, r.style)),
  );
  return configs
    .flatMap((config) => VARIANTS.map((v): ChainOption => ({ config, ...v })))
    .filter((o) => !present.has(key(o.config.id, o.thinking, o.promptStyle)));
}

/** Only the non-default halves are suffixed, matching how the walk names rungs in its logs. */
export function optionLabel(option: ChainOption): string {
  const suffixes = [
    ...(option.promptStyle === AttributionPromptStyle.Simple ? ['simple'] : []),
    ...(option.promptStyle === AttributionPromptStyle.Chapter ? ['chapter'] : []),
    ...(option.thinking ? ['thinking'] : []),
  ];
  return suffixes.length ? `${option.config.name} (${suffixes.join(', ')})` : option.config.name;
}
