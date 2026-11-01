import type { SettingsFieldDto } from '@app/api';

/** A settings field's optional parts, all unset: the host sends each, null when the field has none. */
export const FIELD_UNSET = {
  min: null,
  max: null,
  step: null,
  options: null,
  default: null,
  help: null,
  nullable: false,
} satisfies Partial<SettingsFieldDto>;
