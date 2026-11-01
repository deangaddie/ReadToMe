import type { SettingsFieldDto } from '@app/api';
import type { SettingsField, SettingsFieldKind } from '@app/ui/settings-form';

/**
 * A wire settings field (provider schema, step catalog dial) as the form component's field: wire
 * nulls become absent optionals so `exactOptionalPropertyTypes` is satisfied, and `kind` narrows
 * to the form's kinds. `defaultValue` lets a caller substitute its own default (the active
 * config's value) for the schema's.
 */
export function toFormField(f: SettingsFieldDto, defaultValue: unknown = f.default): SettingsField {
  return {
    key: f.key,
    label: f.label,
    // The host writes one of the five kinds as a plain string (`SettingsFieldDto.Kind`).
    kind: f.kind as SettingsFieldKind,
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
