import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { VoiceDto, toApiError } from '@app/api';
import { SettingsForm, SettingsSchema, SettingsValues } from '@app/ui/settings-form/settings-form';
import { PROVIDER_AREA_LABEL, ProviderArea, ProviderSchemas } from './provider-schemas';
import { overrideDirty, overrideJson, overrideValues } from './voice-logic';

/** Which override column an editor tab edits, and the command that saves it. */
export interface OverrideSave {
  area: ProviderArea;
  /** Null clears the override. */
  json: string | null;
}

/**
 * One tab of the Advanced settings: an `r2m-settings-form` in override mode over the active
 * provider's schema, seeded from the voice's stored patch. Save is dirty-gated and blocked while a
 * field is invalid. Emits the sparse patch as the column value; the parent posts the command.
 */
@Component({
  selector: 'app-voice-override-editor',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, SettingsForm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'voice-override', '[attr.data-area]': 'area()' },
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" aria-label="Loading provider settings" />
    } @else if (error(); as error) {
      <p class="voice-override__note" role="alert">{{ error }}</p>
    } @else if (schema(); as schema) {
      <p class="voice-override__note">
        Overriding the active {{ label() }} provider ({{ schema.type }}). Fields left alone use the
        provider's settings.
      </p>
      <r2m-settings-form
        mode="override"
        [schema]="schema"
        [values]="stored()"
        [disabled]="disabled()"
        (valuesChange)="patch.set($event)"
        (validChange)="valid.set($event)"
      />
      <button
        mat-stroked-button
        type="button"
        data-action="save-override"
        [disabled]="disabled() || !dirty() || !valid()"
        (click)="save.emit({ area: area(), json: json() })"
      >
        <mat-icon>save</mat-icon> Save
      </button>
    } @else {
      <p class="voice-override__note">No active {{ label() }} provider — nothing to override.</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-3) 0;
    }
    .voice-override__note {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    button {
      align-self: flex-start;
    }
  `,
})
export class VoiceOverrideEditor {
  private readonly schemas = inject(ProviderSchemas);

  readonly area = input.required<ProviderArea>();
  /** The stored override column (null = no override). */
  readonly storedJson = input<string | null>(null);
  readonly disabled = input(false);

  readonly save = output<OverrideSave>();

  protected readonly schema = signal<SettingsSchema | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  /** The stored column as form values; the form re-seeds from it whenever the voice reloads. */
  protected readonly stored = computed(() => overrideValues(this.storedJson()));
  protected readonly patch = signal<SettingsValues>({});
  protected readonly valid = signal(true);
  protected readonly dirty = computed(() => overrideDirty(this.storedJson(), this.patch()));
  protected readonly json = computed(() => overrideJson(this.patch()));
  protected readonly label = computed(() => PROVIDER_AREA_LABEL[this.area()]);

  constructor() {
    effect(() => {
      const area = this.area();
      untracked(() => void this.load(area));
    });
    // A reload of the voice (save landed, another tab wrote) resets the working patch to it.
    effect(() => {
      const stored = this.stored();
      untracked(() => this.patch.set(stored));
    });
  }

  private async load(area: ProviderArea): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.schema.set(await this.schemas.active(area));
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }
}

/** Blazor's "Advanced settings": Voice Design and Text-to-Speech override tabs. */
@Component({
  selector: 'app-voice-overrides',
  imports: [MatTabsModule, VoiceOverrideEditor],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-tab-group animationDuration="0ms">
      <mat-tab label="Voice Design">
        <app-voice-override-editor
          area="voice-design"
          [storedJson]="voice().voiceDesignSettingsOverrideJson"
          [disabled]="disabled()"
          (save)="save.emit($event)"
        />
      </mat-tab>
      <mat-tab label="Text-to-Speech">
        <app-voice-override-editor
          area="paragraph-tts"
          [storedJson]="voice().ttsSettingsOverrideJson"
          [disabled]="disabled()"
          (save)="save.emit($event)"
        />
      </mat-tab>
    </mat-tab-group>
  `,
})
export class VoiceOverrides {
  readonly voice = input.required<VoiceDto>();
  readonly disabled = input(false);
  readonly save = output<OverrideSave>();
}
