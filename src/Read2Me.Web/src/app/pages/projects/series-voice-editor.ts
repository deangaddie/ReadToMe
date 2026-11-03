import { html, nothing } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import { repeat } from 'lit-html/directives/repeat.js';
import {
  BookApi,
  type CharacterVoicesDto,
  type Guid,
  type BookCommand,
  type SeriesCharacterDto,
  SeriesApi,
  type VoiceDto,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { provide, use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { LiveService } from '@app/live/live.service';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { blockable, icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { CastStore } from '../cast/cast-store';
import { openAddVoiceDialog } from '../cast/voices/add-voice-dialog';
import { voiceAudioUrl } from '../cast/voices/voice-logic';
import { deleteSeriesVoiceConfirm } from './permanent-changes';
import {
  defaultCells,
  defaultHeading,
  deleteBlockOf,
  deleteNote,
  renameHint,
  savedInLabel,
  seriesCopyBook,
} from './series-voices';
import { adoptStyles } from '@app/core/styles';
import editorCss from './series-voice-editor.css' with { type: 'text' };
import '@app/ui/audio-player';
import '../cast/voices/voice-card';

adoptStyles(editorCss);

/** What every Voice row reads, read once per render. */
interface RowView {
  busy: boolean;
  expanded: string | null;
  versions: Record<Guid, number>;
  fresh: CharacterVoicesDto;
}

/**
 * The inline series Voice editor (#221, Character linking #208 variant B): opened under its row on
 * the series cast board, several at once. It shows the series copy's Voices with Add Voice, play,
 * the full editor (the Cast page's voice card, over the series copy's book), rename, delete — off
 * with its note while another book's rules use the Voice or it is the last — and one "Default in
 * Book N" column per linked book, each that book's own default. Every write goes to the series
 * copy's per-book routes (or, for a default, to that book's); the host copies each Voice edit into
 * the other linked books. Read again on the hub's `series` signal for this series.
 */
export class SeriesVoiceEditor extends R2mElement {
  private readonly api = use(SeriesApi);
  private readonly book = use(BookApi);
  private readonly live = use(LiveService);
  private readonly confirm = use(ConfirmService);
  private readonly prompt = use(PromptService);
  private readonly toast = use(ToastService);
  /** The series copy's book as the voice card sees it: the full editor reads and writes through it. */
  private readonly cast = provide(this, CastStore, new CastStore());

  seriesId = '';
  seriesCharacterId = '';
  /** The row's label, for the heading and the dialogs. */
  label = '';

  readonly detail = signal<SeriesCharacterDto | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  /** The Voice whose full editor is open, if any. */
  readonly expanded = signal<string | null>(null);
  private seq = 0;

  protected override connected(): void {
    this.classList.add('series-voices');
    this.cast.seriesEditor = true;
    this.onDisconnect(
      this.live.on('series', (m) => {
        if (m.seriesId === this.seriesId && m.kind === 'Changed') void this.load();
      }),
    );
    this.onDisconnect(() => this.cast.close());
    void this.load();
  }

  /** Reads the editor; a slower, older read never overwrites a newer one. */
  async load(): Promise<void> {
    if (!this.seriesId || !this.seriesCharacterId) return;
    const seq = ++this.seq;
    try {
      const detail = await this.api.character(this.seriesId, this.seriesCharacterId);
      if (seq !== this.seq) return;
      this.detail.set(detail);
      this.error.set(null);
      await this.follow(detail);
    } catch (error) {
      if (seq !== this.seq) return;
      this.error.set(toApiError(error).message);
    }
  }

  /** Points the full editor's store at the series copy's Character, or reads it again. */
  private async follow(detail: SeriesCharacterDto): Promise<void> {
    const copy = seriesCopyBook(detail);
    if (!copy) return;
    if (this.cast.folder() !== detail.seriesCopyFolder)
      await this.cast.open(detail.seriesCopyFolder);
    if (this.cast.selectedId() !== copy.characterId) await this.cast.select(copy.characterId);
    else await this.cast.refreshVoices();
  }

  /** Runs one command in `folder`; toasts "Saved in N books" (or the refusal), then reads again. */
  private async run(folder: string, command: BookCommand, saved = true): Promise<boolean> {
    const detail = this.detail();
    if (!detail || this.busy()) return false;
    this.busy.set(true);
    try {
      await this.book.execute(folder, command);
      if (saved) this.toast.success(savedInLabel(detail));
      return true;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    } finally {
      this.busy.set(false);
      await this.load();
    }
  }

  async addVoice(): Promise<void> {
    const detail = this.detail();
    const copy = detail && seriesCopyBook(detail);
    if (!detail || !copy) return;
    const choice = await openAddVoiceDialog({ characterName: this.label || copy.characterName });
    if (!choice) return;
    const before = new Set(detail.voices.map((v) => v.id));
    const added = await this.run(detail.seriesCopyFolder, {
      type: 'CreateVoice',
      characterId: copy.characterId,
      name: choice.name,
      isGenerated: choice.isGenerated,
    });
    // Open the new Voice's full editor: its audio is designed or uploaded next.
    const created = added ? this.detail()?.voices.find((v) => !before.has(v.id)) : undefined;
    if (created) this.expanded.set(created.id);
  }

  async rename(voice: VoiceDto): Promise<void> {
    const detail = this.detail();
    if (!detail) return;
    const name = await this.prompt.text({
      title: 'Rename Voice',
      label: 'Name',
      initial: voice.name,
      required: true,
      confirmLabel: 'Rename',
      hint: renameHint(detail),
    });
    if (!name || name === voice.name) return;
    await this.run(detail.seriesCopyFolder, {
      type: 'UpdateVoice',
      voiceId: voice.id,
      name,
      description: voice.description ?? null,
    });
  }

  async deleteVoice(voice: VoiceDto): Promise<void> {
    const detail = this.detail();
    if (!detail || deleteBlockOf(detail, voice)) return;
    const ok = await this.confirm.confirm(
      deleteSeriesVoiceConfirm(
        voice.name,
        detail.books.map((b) => b.title),
      ),
    );
    if (!ok) return;
    if (this.expanded() === voice.id) this.expanded.set(null);
    await this.run(detail.seriesCopyFolder, { type: 'DeleteVoice', voiceId: voice.id });
  }

  /** "Default in Book N": that book's own default rule, so no copy and no "Saved in" toast. */
  async setDefault(folder: string, voice: VoiceDto): Promise<void> {
    await this.run(folder, { type: 'SetVoiceDefault', voiceId: voice.id }, false);
  }

  protected template() {
    const detail = this.detail();
    const error = this.error();
    if (error && !detail)
      return statusChip({
        status: 'error',
        label: `The series Voices could not be read: ${error}`,
      });
    if (!detail)
      return html`<p class="series-voices__note" aria-busy="true">Reading the Voices…</p>`;
    const busy = this.busy();
    // Read here, not inside `repeat`'s item templates, so the rows stay tracked (web.md, Gotchas).
    const view: RowView = {
      busy,
      expanded: this.expanded(),
      versions: this.cast.audioVersions(),
      fresh: this.cast.voices(),
    };
    return html`<section class="series-voices__panel" aria-label=${`${this.label} Voices`}>
      <header class="series-voices__head">
        <h3 class="series-voices__title">
          ${icon('record_voice_over')} ${this.label}: series Voices
        </h3>
        <span class="series-voices__hint"
          >Every change is saved in each linked book, as a History step there.</span
        >
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="add-voice"
          ?disabled=${busy}
          @click=${() => void this.addVoice()}
        >
          ${icon('add')} Add Voice
        </button>
      </header>
      ${
        detail.voices.length === 0
          ? html`<p class="series-voices__note">No Voices yet. Add one to share it.</p>`
          : html`<table class="series-voices__table" aria-busy=${busy ? 'true' : 'false'}>
              <thead>
                <tr>
                  <th scope="col">Voice</th>
                  ${detail.books.map(
                    (b) =>
                      html`<th
                        scope="col"
                        class="series-voices__default-head"
                        data-folder=${b.folder}
                      >
                        ${defaultHeading(b)}
                        <span class="series-voices__as">as ${b.characterName}</span>
                      </th>`,
                  )}
                  <th scope="col" aria-label="Actions"></th>
                </tr>
              </thead>
              <tbody>
                ${repeat(
                  detail.voices,
                  (v) => v.id,
                  (voice) => this.voiceRows(detail, voice, view),
                )}
              </tbody>
            </table>`
      }
      <p class="series-voices__footnote">
        Each book's default is that book's own Voice Rule, set here for convenience. Positional
        rules stay on each book's Cast page.
      </p>
    </section>`;
  }

  private voiceRows(detail: SeriesCharacterDto, voice: VoiceDto, view: RowView) {
    const busy = view.busy;
    const note = deleteNote(deleteBlockOf(detail, voice));
    const open = view.expanded === voice.id;
    const url = voiceAudioUrl(detail.seriesCopyFolder, voice, view.versions);
    return html`<tr data-voice-id=${voice.id}>
        <td class="series-voices__voice">
          <strong class="series-voices__name">${voice.name}</strong>
          ${voice.description ? html`<div class="series-voices__description">${voice.description}</div>` : nothing}
          ${note ? html`<div class="series-voices__blocked" data-testid="delete-note">${icon('info')} ${note}</div>` : nothing}
        </td>
        ${defaultCells(detail, voice).map(
          (cell) =>
            html`<td class="series-voices__default" data-folder=${cell.book.folder}>
              <input
                type="radio"
                name=${`default-${this.seriesCharacterId}-${cell.book.folder}`}
                aria-label=${`Default in ${cell.book.title}`}
                .checked=${live(cell.checked)}
                ?disabled=${busy}
                @change=${() => void this.setDefault(cell.book.folder, voice)}
              />
            </td>`,
        )}
        <td class="series-voices__actions">
          ${
            url
              ? html`<r2m-audio-player
                  .src=${url}
                  .compact=${true}
                  .label=${`Play ${voice.name}`}
                ></r2m-audio-player>`
              : nothing
          }
          <button
            type="button"
            class="r2m-icon-button"
            data-action="edit"
            aria-expanded=${open ? 'true' : 'false'}
            aria-label=${`Edit ${voice.name}`}
            data-tooltip="Edit"
            @click=${() => this.expanded.set(open ? null : voice.id)}
          >
            ${icon('tune')}
          </button>
          <button
            type="button"
            class="r2m-icon-button"
            data-action="rename"
            aria-label=${`Rename ${voice.name}`}
            data-tooltip="Rename"
            ?disabled=${busy}
            @click=${() => void this.rename(voice)}
          >
            ${icon('edit')}
          </button>
          ${blockable(
            note,
            html`<button
              type="button"
              class="r2m-icon-button"
              data-action="delete"
              aria-label=${`Delete ${voice.name}`}
              data-tooltip=${note ? nothing : 'Delete'}
              ?disabled=${busy || note !== null}
              @click=${() => void this.deleteVoice(voice)}
            >
              ${icon('delete')}
            </button>`,
          )}
        </td>
      </tr>
      ${open ? this.fullEditor(detail, voice, view.fresh) : nothing}`;
  }

  /** The Cast page's voice card over the series copy's book, under its row. */
  private fullEditor(detail: SeriesCharacterDto, voice: VoiceDto, fresh: CharacterVoicesDto) {
    const current = fresh.voices.find((v) => v.id === voice.id) ?? voice;
    return html`<tr class="series-voices__editor-row" data-editor-for=${voice.id}>
      <td colspan=${detail.books.length + 2}>
        <r2m-voice-card
          .voice=${current}
          .characterName=${this.label}
          .isDefault=${fresh.defaultVoiceId === voice.id}
        ></r2m-voice-card>
      </td>
    </tr>`;
  }
}
define('r2m-series-voice-editor', SeriesVoiceEditor);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-series-voice-editor': SeriesVoiceEditor;
  }
}
