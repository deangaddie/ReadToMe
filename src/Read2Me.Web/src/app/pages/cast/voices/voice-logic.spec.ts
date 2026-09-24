import { CharacterVoicesDto, VoiceDto } from '@app/api';
import {
  EDITED_OVERWRITE_MESSAGE,
  applyVoiceUpdated,
  canGenerateAudio,
  overrideDirty,
  overrideJson,
  overrideValues,
  overwriteEditWarning,
  promptBatchRequest,
  sourceSwitchWarning,
  voiceAudioUrl,
} from './voice-logic';

function voice(overrides: Partial<VoiceDto> = {}): VoiceDto {
  return {
    id: 'v1',
    characterId: 'alice',
    name: 'Main',
    description: null,
    source: 'Generated',
    designPrompt: null,
    transcript: null,
    audioFileName: null,
    isEdited: false,
    voiceDesignSettingsOverrideJson: null,
    ttsSettingsOverrideJson: null,
    ...overrides,
  };
}

describe('source toggle confirm rule', () => {
  it('needs no confirm when nothing is dropped', () => {
    expect(sourceSwitchWarning(voice({ source: 'Uploaded' }), 'Generated')).toBeNull();
    expect(sourceSwitchWarning(voice({ source: 'Generated' }), 'Uploaded')).toBeNull();
    expect(
      sourceSwitchWarning(voice({ source: 'Generated', designPrompt: 'x' }), 'Generated'),
    ).toBeNull();
  });

  it('confirms when a recording would be dropped by switching to Prompt', () => {
    const w = sourceSwitchWarning(
      voice({ source: 'Uploaded', audioFileName: 'voices/a.wav' }),
      'Generated',
    );
    expect(w).toContain('recording');
  });

  it('confirms when the design prompt would be dropped by switching to Reference', () => {
    const w = sourceSwitchWarning(
      voice({ source: 'Generated', designPrompt: 'warm alto' }),
      'Uploaded',
    );
    expect(w).toContain('design prompt');
  });

  it('warns before overwriting edited audio, never otherwise', () => {
    expect(overwriteEditWarning(voice({ isEdited: true }))).toBe(EDITED_OVERWRITE_MESSAGE);
    expect(overwriteEditWarning(voice())).toBeNull();
  });

  it('generate audio needs a non-blank prompt', () => {
    expect(canGenerateAudio('')).toBe(false);
    expect(canGenerateAudio('   ')).toBe(false);
    expect(canGenerateAudio(null)).toBe(false);
    expect(canGenerateAudio('a voice')).toBe(true);
  });
});

describe('override patch generation', () => {
  it('reads the stored column as values, tolerating null, blank and junk', () => {
    expect(overrideValues(null)).toEqual({});
    expect(overrideValues('')).toEqual({});
    expect(overrideValues('not json')).toEqual({});
    expect(overrideValues('[1]')).toEqual({});
    expect(overrideValues('{"cfg_value": 3.5, "denoise": true}')).toEqual({
      cfg_value: 3.5,
      denoise: true,
    });
  });

  it('writes an empty patch as null (clears the override) and anything else as compact JSON', () => {
    expect(overrideJson({})).toBeNull();
    expect(overrideJson({ cfg_value: 3.5 })).toBe('{"cfg_value":3.5}');
  });

  it('round-trips the column through the form unchanged', () => {
    const stored = '{"cfg_value":3.5,"denoise":true}';
    expect(overrideJson(overrideValues(stored))).toBe(stored);
  });

  it('is dirty only when the patch differs from what is stored', () => {
    expect(overrideDirty(null, {})).toBe(false);
    expect(overrideDirty('{"cfg_value":3.5}', { cfg_value: 3.5 })).toBe(false);
    expect(overrideDirty('{"cfg_value":3.5}', {})).toBe(true);
    expect(overrideDirty(null, { denoise: true })).toBe(true);
  });
});

describe('batch scope dialog outcomes', () => {
  it('starts straight away when no character has a voice, whatever the dialog would say', () => {
    expect(promptBatchRequest(false, null)).toEqual({ regenerateAll: false, confirm: null });
  });

  it('starts nothing when the dialog was cancelled', () => {
    expect(promptBatchRequest(true, null)).toBeNull();
  });

  it('only-without plans the characters without voices, unconfirmed', () => {
    expect(promptBatchRequest(true, 'only-without')).toEqual({
      regenerateAll: false,
      confirm: null,
    });
  });

  it('regenerate-all is destructive: flagged and confirmed first', () => {
    const request = promptBatchRequest(true, 'regenerate-all');
    expect(request?.regenerateAll).toBe(true);
    expect(request?.confirm).toContain('deleted');
  });
});

describe('batch hub events', () => {
  it('voiceUpdated patches the reported fields of a known voice in place', () => {
    const list: CharacterVoicesDto = {
      defaultVoiceId: 'v1',
      voices: [voice({ transcript: 'old', isEdited: true }), voice({ id: 'v2', name: 'Other' })],
    };
    const patched = applyVoiceUpdated(list, {
      kind: 'voiceUpdated',
      characterId: 'alice',
      voiceId: 'v1',
      designPrompt: 'a warm alto',
      audioFileName: 'voices/alice/v1.wav',
    });
    expect(patched).not.toBe(list);
    expect(patched.voices[0]).toEqual(
      voice({
        designPrompt: 'a warm alto',
        audioFileName: 'voices/alice/v1.wav',
        transcript: 'old',
        isEdited: false,
      }),
    );
    expect(patched.voices[1]).toBe(list.voices[1]);
    expect(patched.defaultVoiceId).toBe('v1');
  });

  it('voiceUpdated for an unknown voice answers the same list (reload instead)', () => {
    const list: CharacterVoicesDto = { defaultVoiceId: null, voices: [voice()] };
    expect(applyVoiceUpdated(list, { kind: 'voiceUpdated', voiceId: 'new' })).toBe(list);
  });
});

describe('voice audio url', () => {
  it('is null without audio and cache-busted with the version otherwise', () => {
    expect(voiceAudioUrl('dune', voice(), {})).toBeNull();
    expect(voiceAudioUrl('dune', voice({ audioFileName: 'voices/a/v1.wav' }), {})).toBe(
      '/workspace/dune/voices/a/v1.wav?v=0',
    );
    expect(voiceAudioUrl('dune', voice({ audioFileName: 'voices/a/v1.wav' }), { v1: 2 })).toBe(
      '/workspace/dune/voices/a/v1.wav?v=2',
    );
  });
});
