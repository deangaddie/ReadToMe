import { ProviderSettingsSchema } from '@app/api';
import { toFormSchema } from './provider-schemas';

const WIRE: ProviderSettingsSchema = {
  type: 'VoxCpm2',
  fields: [
    {
      key: 'cfg_value',
      label: 'CFG Value',
      kind: 'number',
      min: 1,
      max: 5,
      step: 0.1,
      default: 2,
      help: 'Guidance',
      nullable: false,
    },
    { key: 'denoise', label: 'Denoise', kind: 'boolean', default: false, nullable: false },
    {
      key: 'top_k',
      label: 'Top K',
      kind: 'number',
      min: 1,
      max: null,
      step: null,
      options: null,
      default: null,
      help: null,
      nullable: true,
    },
  ],
};

describe('toFormSchema', () => {
  it('drops null optionals and keeps nullable only when set', () => {
    const schema = toFormSchema(WIRE);
    expect(schema.fields[0]).toEqual({
      key: 'cfg_value',
      label: 'CFG Value',
      kind: 'number',
      min: 1,
      max: 5,
      step: 0.1,
      default: 2,
      help: 'Guidance',
    });
    expect(schema.fields[2]).toEqual({
      key: 'top_k',
      label: 'Top K',
      kind: 'number',
      min: 1,
      default: null,
      nullable: true,
    });
  });

  it("seeds each default from the active config's settingsJson when it names the key", () => {
    const schema = toFormSchema(WIRE, '{"baseUrl":"http://x","cfg_value":3.5,"denoise":true}');
    expect(schema.fields.map((f) => [f.key, f.default])).toEqual([
      ['cfg_value', 3.5],
      ['denoise', true],
      ['top_k', null],
    ]);
  });

  it('falls back to the recommended defaults on a blank or broken settingsJson', () => {
    expect(toFormSchema(WIRE, '').fields[0]!.default).toBe(2);
    expect(toFormSchema(WIRE, 'nope').fields[0]!.default).toBe(2);
  });
});
