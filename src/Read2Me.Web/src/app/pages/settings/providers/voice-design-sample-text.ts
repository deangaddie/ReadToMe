import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { VoiceDesignSampleText, VoiceDesignSettingsApi, toApiError } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { ToastService } from '@app/ui/toast/toast.service';

/**
 * The sentence every designed voice speaks (ticket 22): Save is dirty-gated, Reset to default only
 * refills the draft — as Blazor's card — and saving the default text stores "no override".
 */
@Component({
  selector: 'app-voice-design-sample-text',
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule, TextFieldModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="sample__title">Sample text</h2>
    <p class="sample__note">The sentence every designed voice speaks.</p>
    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="sample__text">
      <mat-label>Sample text</mat-label>
      <textarea
        matInput
        cdkTextareaAutosize
        cdkAutosizeMinRows="3"
        cdkAutosizeMaxRows="10"
        data-field="sampleText"
        [disabled]="stored() === null || saving()"
        [value]="draft()"
        (input)="draft.set($any($event.target).value)"
      ></textarea>
    </mat-form-field>
    <div class="sample__actions">
      <button
        mat-flat-button
        type="button"
        data-action="save-sample-text"
        [disabled]="!dirty() || saving()"
        (click)="save()"
      >
        Save
      </button>
      <button
        mat-button
        type="button"
        data-action="reset-sample-text"
        [disabled]="isDefault() || saving()"
        (click)="resetToDefault()"
      >
        Reset to default
      </button>
    </div>
    @if (error()) {
      <p class="sample__error" role="alert">{{ error() }}</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .sample__title {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .sample__note {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .sample__text {
      width: 100%;
    }
    .sample__actions {
      display: flex;
      gap: var(--r2m-space-2);
    }
    .sample__error {
      margin: 0;
      color: var(--r2m-status-error);
    }
  `,
})
export class VoiceDesignSampleTextCard {
  private readonly api = inject(VoiceDesignSettingsApi);
  private readonly live = inject(LiveService);
  private readonly toast = inject(ToastService);

  /** Null until the first load lands. */
  protected readonly stored = signal<VoiceDesignSampleText | null>(null);
  protected readonly draft = signal('');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly effective = computed(() => {
    const stored = this.stored();
    return stored ? (stored.text ?? stored.default) : '';
  });
  readonly dirty = computed(() => this.stored() !== null && this.draft() !== this.effective());
  protected readonly isDefault = computed(() => this.draft() === (this.stored()?.default ?? ''));

  constructor() {
    void this.load();
    this.live
      .on('settingsChanged')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (m.area === 'voice-design') void this.load();
      });
  }

  private async load(): Promise<void> {
    try {
      this.accept(await this.api.sampleText());
    } catch (e) {
      this.error.set(`The sample text could not be loaded: ${toApiError(e).message}`);
    }
  }

  /** A reload never wipes a draft in progress. */
  private accept(stored: VoiceDesignSampleText, replaceDraft = !this.dirty()): void {
    this.stored.set(stored);
    if (replaceDraft) this.draft.set(stored.text ?? stored.default);
    this.error.set(null);
  }

  protected resetToDefault(): void {
    this.draft.set(this.stored()?.default ?? '');
  }

  protected async save(): Promise<void> {
    this.saving.set(true);
    try {
      this.accept(await this.api.setSampleText(this.draft()), true);
      this.toast.success('Sample text saved');
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
