import { PreviewStageDto, StepCatalogEntryDto, VoiceDto } from '@app/api';
import {
  applyBlockedReason,
  buildPreviewSteps,
  hissRedundant,
  originalAudioUrl,
  sameRender,
  seedValues,
  stageFor,
  toStepSchema,
} from './editor-logic';

const CATALOG: StepCatalogEntryDto[] = [
  {
    stepId: 'denoise',
    label: 'Denoise',
    blurb: 'Removes broadband room noise and hum.',
    dials: [
      {
        key: 'strength',
        label: 'Strength',
        kind: 'number',
        min: 1,
        max: 1000,
        step: 1,
        default: 20,
        nullable: false,
      },
    ],
    defaults: { strength: 20 },
  },
  {
    stepId: 'hiss-reduce',
    label: 'Hiss reduce',
    blurb: 'Attenuates hiss above 5 kHz only.',
    dials: [
      {
        key: 'preset',
        label: 'Strength',
        kind: 'enum',
        options: [
          { value: 'light', label: 'Light' },
          { value: 'strong', label: null },
        ],
        default: 'light',
        help: null,
        nullable: false,
      },
    ],
    defaults: { preset: 'light' },
  },
  {
    stepId: 'silence-trim',
    label: 'Silence trim',
    blurb: 'Trims dead air from the start and end.',
    dials: [
      {
        key: 'thresholdDb',
        label: 'Threshold (dB)',
        kind: 'number',
        min: -60,
        max: -35,
        step: 1,
        default: -35,
        nullable: false,
      },
      {
        key: 'padMs',
        label: 'Pad (ms)',
        kind: 'number',
        min: 0,
        max: 500,
        step: 10,
        default: 50,
        nullable: false,
      },
    ],
    defaults: { thresholdDb: -35, padMs: 50 },
  },
];

function voice(overrides: Partial<VoiceDto> = {}): VoiceDto {
  return {
    id: 'v1',
    characterId: 'alice',
    name: 'Main',
    description: null,
    source: 'Uploaded',
    designPrompt: null,
    transcript: null,
    audioFileName: 'voices/alice/v1-main.wav',
    isEdited: false,
    voiceDesignSettingsOverrideJson: null,
    ttsSettingsOverrideJson: null,
    ...overrides,
  };
}

describe('toStepSchema', () => {
  it('maps a catalog entry to a settings-form schema, dropping wire nulls', () => {
    const schema = toStepSchema(CATALOG[1]!);
    expect(schema.type).toBe('hiss-reduce');
    expect(schema.fields).toEqual([
      {
        key: 'preset',
        label: 'Strength',
        kind: 'enum',
        options: [{ value: 'light', label: 'Light' }, { value: 'strong' }],
        default: 'light',
      },
    ]);
  });

  it('keeps slider ranges', () => {
    const [threshold] = toStepSchema(CATALOG[2]!).fields;
    expect(threshold).toMatchObject({
      key: 'thresholdDb',
      min: -60,
      max: -35,
      step: 1,
      default: -35,
    });
    expect('help' in threshold!).toBe(false);
  });
});

describe('seedValues', () => {
  it('seeds every step from its catalog defaults', () => {
    expect(seedValues(CATALOG)).toEqual({
      denoise: { strength: 20 },
      'hiss-reduce': { preset: 'light' },
      'silence-trim': { thresholdDb: -35, padMs: 50 },
    });
  });
});

describe('buildPreviewSteps', () => {
  it('sends only ticked steps, in catalog order, with their current dials', () => {
    const steps = buildPreviewSteps(
      CATALOG,
      { 'silence-trim': true, denoise: true, 'hiss-reduce': false },
      { ...seedValues(CATALOG), denoise: { strength: 30 } },
    );
    expect(steps).toEqual([
      { stepId: 'denoise', settings: { strength: 30 } },
      { stepId: 'silence-trim', settings: { thresholdDb: -35, padMs: 50 } },
    ]);
  });
});

describe('sameRender', () => {
  const rendered = [{ stepId: 'denoise', settings: { strength: 30 } }];

  it('matches an identical tick/dial state', () => {
    expect(sameRender(rendered, [{ stepId: 'denoise', settings: { strength: 30 } }])).toBe(true);
  });

  it('is stale after a dial edit', () => {
    expect(sameRender(rendered, [{ stepId: 'denoise', settings: { strength: 31 } }])).toBe(false);
  });

  it('is stale after a tick change', () => {
    expect(sameRender(rendered, [...rendered, { stepId: 'silence-trim', settings: {} }])).toBe(
      false,
    );
    expect(sameRender(rendered, [])).toBe(false);
  });

  it('is never current without a render', () => {
    expect(sameRender(null, rendered)).toBe(false);
  });
});

describe('applyBlockedReason', () => {
  const ok = { anyTicked: true, hasRender: true, stale: false, busy: false };

  it('allows apply when the render matches the current ticks and dials', () => {
    expect(applyBlockedReason(ok)).toBeNull();
  });

  it('explains each block', () => {
    expect(applyBlockedReason({ ...ok, anyTicked: false })).toMatch(/tick/i);
    expect(applyBlockedReason({ ...ok, hasRender: false })).toMatch(/preview/i);
    expect(applyBlockedReason({ ...ok, stale: true })).toMatch(/changed.*preview again/i);
    expect(applyBlockedReason({ ...ok, busy: true })).toMatch(/wait/i);
  });
});

describe('hissRedundant', () => {
  it('flags hiss reduce ticked together with denoise', () => {
    expect(hissRedundant({ denoise: true, 'hiss-reduce': true })).toBe(true);
    expect(hissRedundant({ denoise: false, 'hiss-reduce': true })).toBe(false);
    expect(hissRedundant({ denoise: true })).toBe(false);
  });
});

describe('stageFor', () => {
  const stages: PreviewStageDto[] = [
    { stepId: 'denoise', applied: true, reason: null, url: '/api/previews/p/denoise.wav' },
    {
      stepId: 'silence-trim',
      applied: false,
      reason: 'trimmed result under 1000 ms',
      url: '/api/previews/p/silence-trim.wav',
    },
  ];

  it("finds a step's stage or nothing", () => {
    expect(stageFor(stages, 'silence-trim')?.reason).toBe('trimmed result under 1000 ms');
    expect(stageFor(stages, 'hiss-reduce')).toBeNull();
    expect(stageFor(null, 'denoise')).toBeNull();
  });
});

describe('originalAudioUrl', () => {
  it('plays the stored original once edited, else the live WAV', () => {
    expect(originalAudioUrl('dune', voice())).toBe('/workspace/dune/voices/alice/v1-main.wav');
    expect(originalAudioUrl('dune', voice({ isEdited: true }))).toBe(
      '/api/projects/dune/voices/v1/original.wav',
    );
    expect(originalAudioUrl('dune', voice({ audioFileName: null }))).toBeNull();
  });
});
