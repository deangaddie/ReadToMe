import { ParagraphTtsServiceType } from '@app/api';
import { SettingsField } from '@app/ui/settings-form/settings-form';
import {
  ProviderConfig,
  ProviderForm,
  addSubstitution,
  buildProviderConfig,
  duplicateProviderForm,
  emptyProviderForm,
  fieldValues,
  mergeFieldValues,
  removeSubstitution,
  sameProviderForm,
  setStepEnabled,
  toProviderForm,
  updateSubstitution,
  validateProviderForm,
} from './provider-form';

const FIELDS: SettingsField[] = [
  { key: 'maxChunkChars', label: 'Max chunk chars', kind: 'number', min: 1, step: 1, default: 500 },
  { key: 'cfg_value', label: 'CFG Value', kind: 'number', min: 1, max: 5, step: 0.1, default: 2 },
  { key: 'normalize', label: 'Text Normalization', kind: 'boolean', default: false },
  { key: 'top_k', label: 'Top K', kind: 'number', min: 1, step: 1, default: null, nullable: true },
  { key: 'apiKey', label: 'API key', kind: 'string', default: null },
];

function ttsConfig(overrides: Partial<ProviderConfig> = {}): ProviderConfig {
  return {
    id: 4,
    name: 'Vox',
    type: ParagraphTtsServiceType.VoxCpm2,
    settingsJson: '{"baseUrl":"http://tts:8003","cfg_value":3.5,"maxChunkChars":400}',
    enabledStepIds: ['to-sentence-case', 's1'],
    substitutionSteps: [
      { id: 's2', paragraphTtsServiceConfigId: 4, fromText: 'Mr.', toText: 'Mister', order: 1 },
      { id: 's1', paragraphTtsServiceConfigId: 4, fromText: 'Dr.', toText: 'Doctor', order: 0 },
    ],
    toSentenceCaseConfig: {
      id: 9,
      paragraphTtsServiceConfigId: 4,
      paragraphEnabled: true,
      wordEnabled: false,
      wordMinLength: 7,
    },
    ...overrides,
  };
}

function form(overrides: Partial<ProviderForm> = {}): ProviderForm {
  return { ...toProviderForm(ttsConfig(), true), ...overrides };
}

describe('provider form', () => {
  describe('toProviderForm', () => {
    it('lifts the base URL out of settingsJson whatever its case', () => {
      expect(toProviderForm(ttsConfig(), true).baseUrl).toBe('http://tts:8003');
      const pascal = ttsConfig({ settingsJson: '{"BaseUrl":"http://sim","PassThreshold":0.9}' });
      const f = toProviderForm(pascal, false);
      expect(f.baseUrl).toBe('http://sim');
      expect(f.settings).toEqual({ passthreshold: 0.9 });
    });

    it('survives settingsJson that is blank or not JSON', () => {
      expect(toProviderForm(ttsConfig({ settingsJson: '' }), false).baseUrl).toBe('');
      expect(toProviderForm(ttsConfig({ settingsJson: '{nope' }), false).settings).toEqual({});
    });

    it('orders substitutions and carries the sentence-case options', () => {
      const text = toProviderForm(ttsConfig(), true).text!;
      expect(text.substitutions.map((s) => s.id)).toEqual(['s1', 's2']);
      expect(text.toSentenceCase).toEqual({
        paragraphEnabled: true,
        wordEnabled: false,
        wordMinLength: '7',
      });
    });

    it('starts the sentence-case options at Blazor’s defaults when none are stored', () => {
      const text = toProviderForm(ttsConfig({ toSentenceCaseConfig: null }), true).text!;
      expect(text.toSentenceCase).toEqual({
        paragraphEnabled: true,
        wordEnabled: true,
        wordMinLength: '5',
      });
    });

    it('holds no text-processing state for the other areas', () => {
      expect(toProviderForm(ttsConfig(), false).text).toBeNull();
    });
  });

  describe('schema → form values', () => {
    it('reads stored values by field key, ignoring case, and leaves the rest to defaults', () => {
      const values = fieldValues(FIELDS, { cfg_value: 3.5, maxchunkchars: 400 });
      expect(values).toEqual({ cfg_value: 3.5, maxChunkChars: 400 });
    });

    it('merges what the settings form emits back without losing hidden keys', () => {
      const merged = mergeFieldValues({ carriermaxtargetchars: 30 }, { cfg_value: 4, Normalize: true });
      expect(merged).toEqual({ carriermaxtargetchars: 30, cfg_value: 4, normalize: true });
    });
  });

  describe('validateProviderForm', () => {
    const validate = (f: ProviderForm) => validateProviderForm(f, FIELDS, 'http://localhost:8003');

    it('accepts a complete form', () => {
      expect(validate(form())).toBeNull();
    });

    it('asks for a name, then a base URL, then an absolute one — in Blazor’s words', () => {
      expect(validate(form({ name: '  ' }))).toBe('Name is required.');
      expect(validate(form({ baseUrl: '' }))).toBe('Base URL is required.');
      expect(validate(form({ baseUrl: 'localhost:8003' }))).toBe(
        'Base URL must be a valid absolute URL (e.g. http://localhost:8003).',
      );
    });

    it('names the first tuning field out of range', () => {
      expect(validate(form({ settings: { cfg_value: 9 } }))).toBe(
        'CFG Value must be between 1 and 5.',
      );
      expect(validate(form({ settings: { maxchunkchars: 0 } }))).toBe(
        'Max chunk chars must be 1 or more.',
      );
    });

    it('wants whole numbers where the step is 1, and lets a nullable one stay blank', () => {
      expect(validate(form({ settings: { top_k: 2.5 } }))).toBe('Top K must be a whole number.');
      expect(validate(form({ settings: { top_k: null } }))).toBeNull();
      expect(validate(form({ settings: { maxchunkchars: null } }))).toBe(
        'Max chunk chars must be 1 or more.',
      );
    });

    it('checks the minimum word length only while de-shouting words is on', () => {
      const text = form().text!;
      const bad = { ...text.toSentenceCase, wordEnabled: true, wordMinLength: '0' };
      expect(validate(form({ text: { ...text, toSentenceCase: bad } }))).toBe(
        'Minimum word length must be a whole number of 1 or more.',
      );
      expect(
        validate(form({ text: { ...text, toSentenceCase: { ...bad, wordEnabled: false } } })),
      ).toBeNull();
      const off = setStepEnabled({ ...text, toSentenceCase: bad }, 'to-sentence-case', false);
      expect(validate(form({ text: off }))).toBeNull();
    });
  });

  describe('buildProviderConfig', () => {
    it('writes the base URL and every field’s effective value into settingsJson', () => {
      const built = buildProviderConfig(form({ name: ' Vox ', baseUrl: ' http://tts:8003 ' }), 4, FIELDS);
      expect(built.name).toBe('Vox');
      expect(JSON.parse(built.settingsJson)).toEqual({
        baseUrl: 'http://tts:8003',
        maxChunkChars: 400,
        cfg_value: 3.5,
        normalize: false,
        top_k: null,
        apiKey: null,
      });
    });

    it('keeps a stored key that no field edits', () => {
      const built = buildProviderConfig(form({ settings: { language: 'en' } }), 4, FIELDS);
      expect(JSON.parse(built.settingsJson).language).toBe('en');
    });

    it('stores a blank optional string as null and trims a filled one', () => {
      const blank = buildProviderConfig(form({ settings: { apikey: '  ' } }), 4, FIELDS);
      expect(JSON.parse(blank.settingsJson).apiKey).toBeNull();
      const filled = buildProviderConfig(form({ settings: { apikey: ' k ' } }), 4, FIELDS);
      expect(JSON.parse(filled.settingsJson).apiKey).toBe('k');
    });

    it('numbers substitutions in row order and leaves the sentence-case row id to the host', () => {
      const built = buildProviderConfig(form(), 4, FIELDS);
      expect(built.enabledStepIds).toEqual(['to-sentence-case', 's1']);
      expect(built.substitutionSteps).toEqual([
        { id: 's1', paragraphTtsServiceConfigId: 4, fromText: 'Dr.', toText: 'Doctor', order: 0 },
        { id: 's2', paragraphTtsServiceConfigId: 4, fromText: 'Mr.', toText: 'Mister', order: 1 },
      ]);
      expect(built.toSentenceCaseConfig).toEqual({
        id: 0,
        paragraphTtsServiceConfigId: 4,
        paragraphEnabled: true,
        wordEnabled: false,
        wordMinLength: 7,
      });
    });

    it('drops the sentence-case options when the step is off, as Blazor does', () => {
      const text = setStepEnabled(form().text!, 'to-sentence-case', false);
      expect(buildProviderConfig(form({ text }), 4, FIELDS).toSentenceCaseConfig).toBeNull();
    });

    it('leaves the text-processing properties off a config of another area', () => {
      const built = buildProviderConfig(form({ text: null }), 4, FIELDS);
      expect('enabledStepIds' in built).toBe(false);
      expect('substitutionSteps' in built).toBe(false);
    });
  });

  describe('substitution rows', () => {
    it('adds a row enabled, so it takes effect at once', () => {
      const text = addSubstitution(form().text!, 'new');
      expect(text.substitutions.at(-1)).toEqual({ id: 'new', fromText: '', toText: '' });
      expect(text.enabledStepIds).toContain('new');
    });

    it('edits one row in place', () => {
      const text = updateSubstitution(form().text!, 's2', { toText: 'Master' });
      expect(text.substitutions.map((s) => s.toText)).toEqual(['Doctor', 'Master']);
    });

    it('removes a row together with its enabled id', () => {
      const text = removeSubstitution(form().text!, 's1');
      expect(text.substitutions.map((s) => s.id)).toEqual(['s2']);
      expect(text.enabledStepIds).toEqual(['to-sentence-case']);
    });

    it('enables a step once and disables it without touching the others', () => {
      const once = setStepEnabled(setStepEnabled(form().text!, 's2', true), 's2', true);
      expect(once.enabledStepIds).toEqual(['to-sentence-case', 's1', 's2']);
      expect(setStepEnabled(once, 's1', false).enabledStepIds).toEqual(['to-sentence-case', 's2']);
    });
  });

  describe('sameProviderForm', () => {
    it('ignores key order in the settings and sees a changed row', () => {
      const a = form({ settings: { a: 1, b: 2 } });
      expect(sameProviderForm(a, form({ settings: { b: 2, a: 1 } }))).toBe(true);
      const edited = updateSubstitution(a.text!, 's1', { fromText: 'Doc' });
      expect(sameProviderForm(a, { ...a, text: edited })).toBe(false);
    });
  });

  describe('duplicateProviderForm', () => {
    it('renames the copy and gives its substitutions fresh ids, still enabled', () => {
      let n = 0;
      const copy = duplicateProviderForm(form(), ['Vox'], () => `fresh-${++n}`);
      expect(copy.name).toBe('Vox (copy)');
      expect(copy.text!.substitutions.map((s) => s.id)).toEqual(['fresh-1', 'fresh-2']);
      expect(copy.text!.enabledStepIds).toEqual(['to-sentence-case', 'fresh-1']);
    });
  });

  it('starts a new config empty, with text processing only where the area has it', () => {
    expect(emptyProviderForm(0, true).text).toEqual({
      enabledStepIds: [],
      substitutions: [],
      toSentenceCase: { paragraphEnabled: true, wordEnabled: true, wordMinLength: '5' },
    });
    expect(emptyProviderForm(1, false)).toEqual({
      name: '',
      type: 1,
      baseUrl: '',
      settings: {},
      text: null,
    });
  });
});
