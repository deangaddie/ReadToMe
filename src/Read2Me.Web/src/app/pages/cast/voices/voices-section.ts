import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import {
  type CharacterSeriesLinkDto,
  type CharacterSummaryDto,
  SeriesApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { castDiffersText, resolveConfirm } from '../../projects/series-voices';
import { blockable, callout, emptyState, icon } from '@app/ui/partials';
import { CastStore } from '../cast-store';
import { openAddVoiceDialog } from './add-voice-dialog';
import { seriesBoardPath } from '../cast-path';
import './voice-card';

/**
 * The Voices section of the character detail (research §4): Add voice, then one card per voice
 * with the default marked. The list comes from the {@link CastStore}, which reloads it on every
 * command and patches it in place while a voice batch runs.
 */
export class VoicesSection extends R2mElement {
  private store!: CastStore;
  private readonly series = use(SeriesApi);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  readonly resolving = signal(false);

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
    const readOnly = this.store.voicesReadOnly();
    const link = this.store.seriesEditor ? null : character.seriesLink;
    return html`
      <div class="voices-section__header">
        <h3 class="voices-section__heading">
          Voices <span class="voices-section__count">${voices.voices.length}</span>
        </h3>
        ${
          link
            ? html`<a
                class="r2m-button r2m-button--stroked"
                data-action="edit-on-board"
                href=${seriesBoardPath(link.seriesId, link.seriesCharacterId)}
              >
                ${icon('library_books')} Edit on the series cast board
              </a>`
            : nothing
        }
        ${blockable(
          readOnly,
          html`<button
            type="button"
            class="r2m-button"
            data-action="add-voice"
            ?disabled=${busy || readOnly !== null}
            @click=${() => void this.addVoice()}
          >
            ${icon('add')} Add voice
          </button>`,
        )}
      </div>
      ${link?.differs ? this.differsCallout(character.name, link) : nothing}
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

  /** Differs from series (#222): this book's copy isn't the series copy; push it, or take that one. */
  private differsCallout(name: string, link: CharacterSeriesLinkDto) {
    const disabled = this.resolving();
    return callout({
      kind: 'warn',
      text: castDiffersText(name, link.seriesName),
      testId: 'differs-callout',
      action: {
        label: 'Use this book’s copy for all',
        name: 'push',
        disabled,
        run: () => void this.resolve(name, link, 'push'),
      },
      more: [
        {
          label: 'Use the series copy',
          name: 'take',
          disabled,
          run: () => void this.resolve(name, link, 'take'),
        },
      ],
    });
  }

  /** Reads the confirm's book titles and what would be removed, confirms, then pushes or takes. */
  async resolve(name: string, link: CharacterSeriesLinkDto, how: 'push' | 'take'): Promise<void> {
    const folder = this.store.folder();
    if (!folder || this.resolving()) return;
    this.resolving.set(true);
    try {
      const [detail, preview] = await Promise.all([
        this.series.character(link.seriesId, link.seriesCharacterId),
        how === 'push'
          ? this.series.previewPush(link.seriesId, link.seriesCharacterId, folder)
          : this.series.previewTake(link.seriesId, link.seriesCharacterId, folder),
      ]);
      const confirm = resolveConfirm(detail, folder, name, how, preview.losses);
      if (!confirm || !(await this.confirm.confirm(confirm))) return;
      if (how === 'push') await this.series.push(link.seriesId, link.seriesCharacterId, folder);
      else await this.series.take(link.seriesId, link.seriesCharacterId, folder);
      // Read again here: a push writes only the other books, so no receipt reaches this one, yet
      // this book's `differs` has changed.
      await this.store.refresh();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.resolving.set(false);
    }
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
