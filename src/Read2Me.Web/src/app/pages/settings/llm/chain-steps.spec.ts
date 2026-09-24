import {
  AttributionChainStep,
  AttributionPromptStyle,
  LlmApiType,
  LlmServerConfig,
} from '@app/api';
import { addStep, chainOptions, chainRows, moveStep, optionLabel, removeStep } from './chain-steps';

const { Full, Simple } = AttributionPromptStyle;

function config(
  id: number,
  name: string,
  promptStyle: AttributionPromptStyle = Full,
): LlmServerConfig {
  return {
    id,
    name,
    apiType: LlmApiType.OpenAiCompatible,
    baseUrl: 'http://localhost:8080',
    apiKey: null,
    model: `${name}-model`,
    temperature: null,
    topP: null,
    maxTokens: null,
    frequencyPenalty: null,
    presencePenalty: null,
    attributionBatchSize: 1,
    promptStyle,
    supportsModelSwitch: false,
  };
}

const step = (
  configId: number,
  thinking = false,
  promptStyle: AttributionPromptStyle | null = Full,
): AttributionChainStep => ({ configId, thinking, promptStyle });

describe('attribution chain steps', () => {
  const small = config(1, 'small');
  const big = config(2, 'big', Simple);

  describe('reorder', () => {
    const steps = [step(1), step(2), step(1, true)];

    it('swaps a step with its neighbour', () => {
      expect(moveStep(steps, 1, -1)).toEqual([step(2), step(1), step(1, true)]);
      expect(moveStep(steps, 1, +1)).toEqual([step(1), step(1, true), step(2)]);
    });

    it('is a no-op at the ends and for an index outside the chain', () => {
      expect(moveStep(steps, 0, -1)).toBe(steps);
      expect(moveStep(steps, 2, +1)).toBe(steps);
      expect(moveStep(steps, 7, -1)).toBe(steps);
    });

    it('does not mutate its input', () => {
      const before = structuredClone(steps);
      moveStep(steps, 0, +1);
      removeStep(steps, 0);
      expect(steps).toEqual(before);
    });
  });

  it('removes by index, including the first step', () => {
    expect(removeStep([step(1), step(2)], 0)).toEqual([step(2)]);
    expect(removeStep([step(1)], 3)).toEqual([step(1)]);
  });

  it('appends a step unless that exact variant is already present', () => {
    const steps = [step(1)];
    expect(addStep(steps, step(1, true))).toEqual([step(1), step(1, true)]);
    expect(addStep(steps, step(1))).toBe(steps);
  });

  it('rows resolve each step to its config and effective style, dropping orphans', () => {
    const rows = chainRows([step(2, true, null), step(9), step(1, false, Simple)], [small, big]);

    expect(rows.map((r) => [r.index, r.config.name, r.thinking, r.simple])).toEqual([
      [0, 'big', true, true], // inherits big's Simple
      [2, 'small', false, true],
    ]);
  });

  it('offers every config in four variants minus the ones in the chain by effective style', () => {
    const options = chainOptions([step(2, false, null), step(1, true, Full)], [small, big]);

    expect(options.map(optionLabel)).toEqual([
      'small',
      'small (simple)',
      'small (simple, thinking)',
      'big',
      'big (thinking)',
      'big (simple, thinking)',
    ]);
  });
});
