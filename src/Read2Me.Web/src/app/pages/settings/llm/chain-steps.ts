import { AttributionChainStep, AttributionPromptStyle, LlmServerConfig } from '@app/api';

/**
 * Pure edits over the stored attribution chain (Blazor's `AttributionEscalationPresenter`). Steps
 * are addressed by index, not config id: one config may hold several rungs differing only in
 * thinking and prompt style. Both flags are fixed at add time.
 */

/** One rendered rung; `index` addresses the stored step, which orphans make sparse. */
export interface ChainRow {
  index: number;
  config: LlmServerConfig;
  thinking: boolean;
  simple: boolean;
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
    const simple = effectiveStyle(step, config) === AttributionPromptStyle.Simple;
    return [{ index, config, thinking: step.thinking, simple }];
  });
}

/**
 * Every config in four variants (full/simple × fast/thinking) minus the ones already in the chain.
 * Compared on the effective style, so a legacy step with no stored style occupies the variant it
 * actually runs as.
 */
export function chainOptions(
  steps: readonly AttributionChainStep[],
  configs: readonly LlmServerConfig[],
): ChainOption[] {
  const key = (id: number, thinking: boolean, style: AttributionPromptStyle) =>
    `${id}:${thinking}:${style}`;
  const present = new Set(
    chainRows(steps, configs).map((r) =>
      key(
        r.config.id,
        r.thinking,
        r.simple ? AttributionPromptStyle.Simple : AttributionPromptStyle.Full,
      ),
    ),
  );
  return configs
    .flatMap((config) =>
      [false, true].flatMap((thinking) =>
        [AttributionPromptStyle.Full, AttributionPromptStyle.Simple].map(
          (promptStyle): ChainOption => ({ config, thinking, promptStyle }),
        ),
      ),
    )
    .filter((o) => !present.has(key(o.config.id, o.thinking, o.promptStyle)));
}

/** Only the non-default halves are suffixed, matching how the walk names rungs in its logs. */
export function optionLabel(option: ChainOption): string {
  const suffixes = [
    ...(option.promptStyle === AttributionPromptStyle.Simple ? ['simple'] : []),
    ...(option.thinking ? ['thinking'] : []),
  ];
  return suffixes.length ? `${option.config.name} (${suffixes.join(', ')})` : option.config.name;
}
