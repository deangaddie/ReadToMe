import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { CharacterSummaryDto } from '@app/api';
import { CastStore } from '../cast-store';
import { openAddVoiceDialog } from './add-voice-dialog';
import { VoiceCard } from './voice-card';

/**
 * The Voices section of the character detail (research §4): Add voice, then one card per voice
 * with the default marked. The list comes from the {@link CastStore}, which reloads it on every
 * command and patches it in place while a voice batch runs.
 */
@Component({
  selector: 'app-voices-section',
  imports: [MatButtonModule, MatIconModule, EmptyState, VoiceCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'voices-section' },
  template: `
    <div class="voices-section__header">
      <h3 class="voices-section__heading">
        Voices
        <span class="voices-section__count">{{ store.voices().voices.length }}</span>
      </h3>
      <button
        mat-button
        type="button"
        data-action="add-voice"
        [disabled]="store.busy()"
        (click)="addVoice()"
      >
        <mat-icon>add</mat-icon> Add voice
      </button>
    </div>
    @if (store.voices().voices.length === 0 && !store.voicesLoading()) {
      <r2m-empty-state
        icon="record_voice_over"
        headline="No voices yet"
        hint="Add one, or generate voice prompts for the whole cast from the toolbar."
        compact
      />
    }
    <div class="voices-section__list">
      @for (voice of store.voices().voices; track voice.id) {
        <app-voice-card
          [voice]="voice"
          [characterName]="character().name"
          [isDefault]="voice.id === store.voices().defaultVoiceId"
        />
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .voices-section__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--r2m-space-2);
    }
    .voices-section__heading {
      margin: 0;
      font-size: var(--r2m-text-md);
      font-weight: 500;
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .voices-section__count {
      color: var(--r2m-text-muted);
      font-weight: 400;
    }
    .voices-section__list {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
  `,
})
export class VoicesSection {
  protected readonly store = inject(CastStore);
  private readonly dialog = inject(MatDialog);

  readonly character = input.required<CharacterSummaryDto>();

  protected async addVoice(): Promise<void> {
    const character = this.character();
    const choice = await openAddVoiceDialog(this.dialog, { characterName: character.name });
    if (!choice) return;
    await this.store.run({
      type: 'CreateVoice',
      characterId: character.id,
      name: choice.name,
      isGenerated: choice.isGenerated,
    });
  }
}
