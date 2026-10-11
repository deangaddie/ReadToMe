import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { CharacterSummaryDto } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { emptyState, icon } from '@app/ui/partials';
import { CastStore } from '../cast-store';
import { openAddVoiceDialog } from './add-voice-dialog';
import './voice-card';

/**
 * The Voices section of the character detail (research §4): Add voice, then one card per voice
 * with the default marked. The list comes from the {@link CastStore}, which reloads it on every
 * command and patches it in place while a voice batch runs.
 */
export class VoicesSection extends R2mElement {
  private store!: CastStore;

  readonly #character = signal<CharacterSummaryDto | null>(null);
  get character() {
    return this.#character();
  }
  set character(value: CharacterSummaryDto | null) {
    this.#character.set(value);
  }

  protected override connected(): void {
    this.classList.add('voices-section');
    this.store = use(CastStore, this);
  }

  protected template() {
    const character = this.#character();
    if (!character) return nothing;
    const voices = this.store.voices();
    const busy = this.store.busy();
    return html`
      <div class="voices-section__header">
        <h3 class="voices-section__heading">
          Voices <span class="voices-section__count">${voices.voices.length}</span>
        </h3>
        <button
          type="button"
          class="r2m-button"
          data-action="add-voice"
          ?disabled=${busy}
          @click=${() => void this.addVoice()}
        >
          ${icon('add')} Add voice
        </button>
      </div>
      ${
        voices.voices.length === 0 && !this.store.voicesLoading()
          ? emptyState({
              icon: 'record_voice_over',
              headline: 'No voices yet',
              hint: 'Add one, or generate voice prompts for the whole cast from the toolbar.',
              compact: true,
            })
          : nothing
      }
      <div class="voices-section__list">
        ${repeat(
          voices.voices,
          (voice) => voice.id,
          (voice) =>
            html`<r2m-voice-card
              .voice=${voice}
              .characterName=${character.name}
              .isDefault=${voice.id === voices.defaultVoiceId}
            ></r2m-voice-card>`,
        )}
      </div>
    `;
  }

  async addVoice(): Promise<void> {
    const character = this.#character();
    if (!character) return;
    const choice = await openAddVoiceDialog({ characterName: character.name });
    if (!choice) return;
    await this.store.run({
      type: 'CreateVoice',
      characterId: character.id,
      name: choice.name,
      isGenerated: choice.isGenerated,
    });
  }
}
define('r2m-voices-section', VoicesSection);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voices-section': VoicesSection;
  }
}
