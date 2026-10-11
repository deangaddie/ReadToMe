import { html, nothing } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import { styleMap } from 'lit-html/directives/style-map.js';
import {
  COVER_ACCEPT,
  COVER_MAX_BYTES,
  ProjectsApi,
  type UpdateProjectRequest,
  toApiError,
  workspaceUrl,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { type ReadonlySignal, computed, signal } from '@app/core/signals';
import type { RejectedFile } from '@app/ui/file-drop';
import { keyValue } from '@app/ui/key-value';
import { icon, statusChip } from '@app/ui/partials';
import { placeholderGradient, projectInitials } from '@app/ui/project-card';
import { ToastService } from '@app/ui/toast';
import { ProjectStore } from './project-store';
import '@app/ui/file-drop';
import '@app/ui/inline-edit';

/**
 * The project card beside the stepper (design §6.2): cover upload/remove, inline title and author,
 * file name and type, the narrator-only policy and who narrates. Every write waits for the host:
 * the PATCH answers with the new detail, the others refetch it. Reads the {@link ProjectStore}
 * the project shell provides.
 */
export class ProjectDetailsPanel extends R2mElement {
  private store!: ProjectStore;
  private readonly api = use(ProjectsApi);
  private readonly toast = use(ToastService);

  readonly busy = signal(false);
  /** Bumped after an upload: a replaced cover may keep its file name. */
  private readonly coverVersion = signal<number | null>(null);

  /** Built in `connected()`, once the shell's store is reachable through the DOM. */
  private coverUrl!: ReadonlySignal<string | null>;

  protected override connected(): void {
    this.classList.add('details');
    this.store = use(ProjectStore, this);
    this.coverUrl = computed(() => {
      const d = this.store.detail();
      return d?.coverImage ? workspaceUrl(d.folderName, d.coverImage, this.coverVersion()) : null;
    });
  }

  protected template() {
    const d = this.store.detail();
    if (!d) return nothing;
    const coverUrl = this.coverUrl();
    const busy = this.busy();
    const castBase = `projects/${encodeURIComponent(d.folderName)}/cast`;
    const castLink = d.narrator.isLinked
      ? `${castBase}/${encodeURIComponent(d.narrator.characterId)}`
      : castBase;
    return html`
      <div
        class="details__cover"
        style=${styleMap({ background: coverUrl ? null : placeholderGradient(d.folderName) })}
      >
        ${
          coverUrl
            ? html`<img class="details__image" src=${coverUrl} alt=${`${d.title} cover`} />`
            : html`<span class="details__initials" aria-hidden="true"
                >${projectInitials(d.title)}</span
              >`
        }
      </div>
      <div class="details__cover-actions">
        <r2m-file-drop
          class="details__drop"
          .accept=${COVER_ACCEPT}
          .maxBytes=${COVER_MAX_BYTES}
          .label=${d.coverImage ? 'Replace cover' : 'Add a cover'}
          .hint=${'JPG, PNG or WebP, up to 10 MB'}
          @files=${(e: Event) => void this.uploadCover((e as CustomEvent<File[]>).detail)}
          @rejected=${(e: Event) => this.rejectCover((e as CustomEvent<RejectedFile[]>).detail)}
        ></r2m-file-drop>
        ${
          d.coverImage
            ? html`<button
                type="button"
                class="r2m-button details__remove-cover"
                ?disabled=${busy}
                @click=${() => void this.removeCover()}
              >
                ${icon('hide_image')} Remove cover
              </button>`
            : nothing
        }
      </div>

      <div class="details__field">
        <span class="details__label">Title</span>
        <r2m-inline-edit
          class="details__title"
          .value=${d.title}
          .placeholder=${'Title'}
          .required=${true}
          @save=${(e: Event) => void this.update({ title: (e as CustomEvent<string>).detail })}
        ></r2m-inline-edit>
      </div>
      <div class="details__field">
        <span class="details__label">Author</span>
        <r2m-inline-edit
          class="details__author"
          .value=${d.author}
          .placeholder=${'Author'}
          @save=${(e: Event) => void this.update({ author: (e as CustomEvent<string>).detail })}
        ></r2m-inline-edit>
      </div>

      ${keyValue(
        [
          { label: 'Book title', value: d.bookTitle },
          { label: 'File', value: d.filename, mono: true },
        ],
        { dense: true },
      )}
      <span class="details__type">${statusChip({ status: 'info', label: d.fileType, compact: true })}</span>

      <!-- live(): a refused PUT refetches the same value, and the box must still snap back. -->
      <label class="r2m-switch details__narrator-only">
        <input
          type="checkbox"
          role="switch"
          name="narratorOnly"
          .checked=${live(d.narratorOnlyMode)}
          ?disabled=${busy}
          @change=${(e: Event) => void this.setNarratorOnly((e.target as HTMLInputElement).checked)}
        />
        Narrator only
      </label>
      <p class="details__hint">Only narration gets audio; dialog is read by the narrator.</p>

      <p class="details__narrator">
        ${icon('record_voice_over')}
        <span
          >${d.narrator.isLinked ? `Narrated by ${d.narrator.displayName}` : 'Own narrator voice'}</span
        >
        <a class="details__cast-link" href=${castLink}>Cast</a>
      </p>
    `;
  }

  async update(request: UpdateProjectRequest): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => this.store.setDetail(await this.api.update(folder, request)));
  }

  async setNarratorOnly(enabled: boolean): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => {
      await this.api.setNarratorOnlyMode(folder, enabled);
      await this.store.refreshDetail();
    });
  }

  async uploadCover(files: File[]): Promise<void> {
    const folder = this.store.folder();
    const file = files[0];
    if (!folder || !file) return;
    await this.write(async () => {
      await this.api.uploadCover(folder, file);
      this.coverVersion.set(Date.now());
      await this.store.refreshDetail();
    });
  }

  rejectCover(rejected: RejectedFile[]): void {
    const reason = rejected[0]?.reason;
    this.toast.warn(
      reason === 'size'
        ? 'Cover image must be 10 MB or smaller.'
        : 'Use a .jpg, .png or .webp image.',
    );
  }

  async removeCover(): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    await this.write(async () => {
      await this.api.deleteCover(folder);
      await this.store.refreshDetail();
    });
  }

  private async write(command: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await command();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      // A refused toggle or edit must snap back to what the host holds.
      await this.store.refreshDetail().catch(() => undefined);
    } finally {
      this.busy.set(false);
    }
  }
}
define('r2m-project-details-panel', ProjectDetailsPanel);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-project-details-panel': ProjectDetailsPanel;
  }
}
