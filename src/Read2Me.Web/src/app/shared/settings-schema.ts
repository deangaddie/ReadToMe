import { ProviderSettingsField } from '@app/api';
import { SettingsField } from '@app/ui/settings-form/settings-form';

/**
 * A wire settings field (provider schema, step catalog dial) as the form component's field: wire
 * nulls become absent optionals so `exactOptionalPropertyTypes` is satisfied. `defaultValue` lets
 * a caller substitute its own default (the active config's value) for the schema's.
 */
export function toFormField(
  f: ProviderSettingsField,
  defaultValue: unknown = f.default,
): SettingsField {
  return {
    key: f.key,
    label: f.label,
    kind: f.kind,
    ...(f.min != null ? { min: f.min } : {}),
    ...(f.max != null ? { max: f.max } : {}),
    ...(f.step != null ? { step: f.step } : {}),
    ...(f.options
      ? {
          options: f.options.map((o) => ({
            value: o.value,
            ...(o.label ? { label: o.label } : {}),
          })),
        }
      : {}),
    default: defaultValue,
    ...(f.help ? { help: f.help } : {}),
    ...(f.nullable ? { nullable: true } : {}),
  };
}
