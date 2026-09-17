import { Injectable, inject } from '@angular/core';
import { ParagraphTtsSettingsApi, ProviderSettingsSchema, VoiceDesignSettingsApi } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { SettingsSchema } from '@app/ui/settings-form/settings-form';
import { overrideValues } from './voice-logic';

export type ProviderArea = 'paragraph-tts' | 'voice-design';

/** What the override editor calls each area. */
export const PROVIDER_AREA_LABEL: Record<ProviderArea, string> = {
  'paragraph-tts': 'text-to-speech',
  'voice-design': 'voice design',
};

/**
 * The active provider's settings schema per area, resolved once and shared by every voice card
 * (`GET /active` then `GET /schema?type=`), dropped when the hub says that settings area changed.
 * The schema's defaults are the **active config's** values (its `settingsJson` merged over the
 * provider's recommended defaults), so "provider default" on a card means what that config would
 * use — as Blazor seeds its override editors. Null when no config is active — the editor then
 * says so instead of rendering fields.
 */
@Injectable({ providedIn: 'root' })
export class ProviderSchemas {
  private readonly tts = inject(ParagraphTtsSettingsApi);
  private readonly voiceDesign = inject(VoiceDesignSettingsApi);
  private readonly live = inject(LiveService);

  private readonly cache = new Map<ProviderArea, Promise<SettingsSchema | null>>();

  constructor() {
    this.live.on('settingsChanged').subscribe((m) => {
      if (m.area === 'paragraph-tts' || m.area === 'voice-design') this.cache.delete(m.area);
    });
  }

  active(area: ProviderArea): Promise<SettingsSchema | null> {
    let pending = this.cache.get(area);
    if (!pending) {
      pending = this.load(area).catch((e: unknown) => {
        this.cache.delete(area);
        throw e;
      });
      this.cache.set(area, pending);
    }
    return pending;
  }

  private async load(area: ProviderArea): Promise<SettingsSchema | null> {
    if (area === 'paragraph-tts') {
      const config = await this.tts.active();
      if (!config) return null;
      return toFormSchema(await this.tts.schema(config.type), config.settingsJson);
    }
    const config = await this.voiceDesign.active();
    if (!config) return null;
    return toFormSchema(await this.voiceDesign.schema(config.type), config.settingsJson);
  }
}

/**
 * The wire schema as the form component's schema: nulls become absent optionals, and each field's
 * default is the active config's value when its `settingsJson` names the key (else the schema's
 * recommended default).
 */
export function toFormSchema(
  schema: ProviderSettingsSchema,
  configSettingsJson: string | null = null,
): SettingsSchema {
  const configured = overrideValues(configSettingsJson);
  return {
    type: schema.type,
    fields: schema.fields.map((f) => ({
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
      default: Object.prototype.hasOwnProperty.call(configured, f.key)
        ? configured[f.key]
        : f.default,
      ...(f.help ? { help: f.help } : {}),
      ...(f.nullable ? { nullable: true } : {}),
    })),
  };
}
