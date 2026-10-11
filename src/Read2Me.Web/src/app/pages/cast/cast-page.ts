import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { ActivityStore } from '@app/activity/activity-store';
import {
  type CharacterSummaryDto,
  type Guid,
  type NarratorDto,
  VoicesApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { provide, use } from '@app/core/services';
import { type ReadonlySignal, computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { emptyState, icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { ProjectStore } from '../project/project-store';
import {
  CAST_SORTS,
  CAST_SORT_LABELS,
  type CastSort,
  displayName,
  filterRows,
  hasBookIcon,
  readinessChip,
  sortRows,
} from './cast-rows';
import { castPath } from './cast-path';
import { CastStore } from './cast-store';
import { openDiscoveryDialog } from './discovery-dialog';
import { narratorSignpost } from './narrator-signpost';
import { promptBatchRequest } from './voices/voice-logic';
import { openVoiceScopeDialog } from './voices/voice-scope-dialog';
import './character-detail';
import './narrator-banner';
import castCss from './cast.css' with { type: 'text' };

adoptStyles(castCss);

/**
 * `/projects/{folder}/cast[/{characterId}]` (design §6.4, ticket 15): the roster on the left —
 * search, sort, narrator banner, rows with readiness — and the selected character on the right.
 * On narrow screens the list is the page and the detail is the pushed route. The toolbar starts the
 * two voice batches (16): prompts behind the scope dialog when voices exist, audio directly.
 * `?discover=1` (from the overview's pipeline) opens the discovery dialog on arrival. Provides the
 * {@link CastStore} to the detail and reads the project shell's {@link ProjectStore}.
 */
export class CastPage extends R2mElement {
  private readonly store = provide(this, CastStore, new CastStore());
  private readonly router = use(Router);
  private readonly voices = use(VoicesApi);
  private readonly live = use(LiveService);
  private readonly activity = use(ActivityStore);
  private readonly confirm = use(ConfirmService);
  private readonly preflight = use(Preflight);
  private readonly prompt = use(PromptService);
  private readonly toast = use(ToastService);
  private project!: ProjectStore;

  readonly query = signal('');
  readonly sort = signal<CastSort>('name');
  /** The seed Narrator row's own (unused) voices, listed by the signpost. */
  readonly narratorVoices = signal<string[]>([]);

  /** Route param; absent on `/cast`. */
  private readonly characterId = computed<Guid | null>(
    () => this.router.params()['characterId'] ?? null,
  );
  /** `?discover=1`: open the discovery dialog on arrival, then drop the param. */
  private readonly discover = computed(() => this.router.query().get('discover'));
  /** Discovery and creation are off while a write is in flight or a voice batch runs (research §4). */
  private readonly toolbarLocked = computed(
    () => this.store.busy() || this.live.voiceBatch().isRunning,
  );

  /** Built in `connected()`, once the shell's store is reachable through the DOM. */
  private folder!: ReadonlySignal<string>;
  private narrator!: ReadonlySignal<NarratorDto | null>;
  private visibleRows!: ReadonlySignal<CharacterSummaryDto[]>;

  protected override connected(): void {
    this.classList.add('cast-page');
    this.project = use(ProjectStore, this);
    this.folder = computed(() => this.project.folder() ?? '');
    this.narrator = computed(() => this.project.detail()?.narrator ?? null);
    this.visibleRows = computed(() =>
      sortRows(filterRows(this.store.rows(), this.query()), this.sort()),
    );

    this.effect(() => {
      const folder = this.project.folder();
      if (!folder) return;
      untracked(() => void this.store.open(folder));
    });
    this.onDisconnect(() => this.store.close());

    // The selection follows the route, once the roster's folder is open.
    this.effect(() => {
      const id = this.characterId();
      this.store.folder();
      // A failed lines read leaves the roster up and the Lines section saying it has none; Angular
      // let the same rejection reach the console.
      untracked(() => void this.store.select(id).catch(() => undefined));
    });

    // The Narrator signpost lists the seed row's own (unused) voices.
    this.effect(() => {
      const selected = this.store.selected();
      const folder = this.store.folder();
      if (!folder || !selected?.isNarrator || !this.narrator()?.isLinked) return;
      untracked(() => void this.loadNarratorVoices(folder, selected.id));
    });

    // Waits for the roster: the dialog folds rows onto it and checks collisions against it.
    this.effect(() => {
      if (this.discover() !== '1' || !this.store.folder() || this.store.loading()) return;
      untracked(() => {
        this.router.navigate(this.router.path(), { query: { discover: null }, replace: true });
        void this.runDiscovery();
      });
    });
  }

  protected template() {
    const error = this.store.error();
    const characterId = this.characterId();
    const narrator = this.narrator();
    const rows = this.store.rows();
    const busy = this.store.busy();
    const toolbarLocked = this.toolbarLocked();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Cast</h1>
        ${
          this.project.detail()?.title
            ? html`<p class="r2m-page-header__subtitle">${this.project.detail()?.title}</p>`
            : nothing
        }
      </header>

      ${error ? statusChip({ status: 'error', label: `Cast could not be loaded: ${error}` }) : nothing}

      <div class="cast ${characterId ? 'cast--detail' : ''}">
        <section class="cast__list" aria-label="Characters">
          <div class="cast__toolbar">
            <button
              type="button"
              class="r2m-button r2m-button--filled"
              data-action="discover"
              ?disabled=${toolbarLocked}
              @click=${() => void this.runDiscovery()}
            >
              ${icon('auto_awesome')} Discover
            </button>
            <button
              type="button"
              class="r2m-button r2m-button--stroked"
              data-action="generate-prompts"
              ?disabled=${toolbarLocked}
              data-tooltip="One LLM voice plan per character; progress in the activity centre"
              @click=${() => void this.generatePrompts()}
            >
              ${icon('record_voice_over')} Generate voice prompts
            </button>
            <button
              type="button"
              class="r2m-button r2m-button--stroked"
              data-action="generate-audio"
              ?disabled=${toolbarLocked}
              data-tooltip="Synthesise every prompt voice without audio; progress in the activity centre"
              @click=${() => void this.generateAudio()}
            >
              ${icon('graphic_eq')} Generate audio
            </button>
            <button
              type="button"
              class="r2m-button r2m-button--stroked"
              data-action="add-character"
              ?disabled=${toolbarLocked}
              @click=${() => void this.addCharacter()}
            >
              ${icon('person_add')} Add character
            </button>
          </div>

          ${
            narrator
              ? html`<r2m-narrator-banner
                  .narrator=${narrator}
                  .rows=${rows}
                  .busy=${busy}
                  @narrator-changed=${(e: Event) =>
                    void this.setNarrator((e as CustomEvent<Guid | null>).detail)}
                ></r2m-narrator-banner>`
              : nothing
          }

          <div class="cast__filters">
            <label class="r2m-field cast__search">
              <input
                type="search"
                placeholder="Search"
                aria-label="Search characters"
                .value=${this.query()}
                @input=${(e: Event) => this.query.set((e.target as HTMLInputElement).value)}
              />
            </label>
            <label class="r2m-field cast__sort">
              <span class="r2m-field__label">Sort</span>
              <select
                aria-label="Sort"
                .value=${this.sort()}
                @change=${(e: Event) =>
                  this.sort.set((e.target as HTMLSelectElement).value as CastSort)}
              >
                ${CAST_SORTS.map(
                  (s) =>
                    html`<option value=${s} ?selected=${s === this.sort()}>
                      ${CAST_SORT_LABELS[s]}
                    </option>`,
                )}
              </select>
            </label>
          </div>

          ${this.roster(rows, characterId, narrator)}
        </section>

        <section class="cast__detail" aria-label="Character">${this.detail(characterId)}</section>
      </div>
    `;
  }

  private roster(
    rows: readonly CharacterSummaryDto[],
    characterId: Guid | null,
    narrator: NarratorDto | null,
  ) {
    if (this.store.loading() && rows.length === 0) {
      return html`<div
        class="cast__skeleton"
        aria-busy="true"
        aria-label="Loading characters"
      ></div>`;
    }
    if (rows.length === 0) {
      return emptyState({
        icon: 'groups',
        headline: 'No characters yet',
        hint: 'Discover them with the LLM, or add one by hand.',
        compact: true,
      });
    }
    const visible = this.visibleRows();
    const folder = this.folder();
    return html`<ul class="cast__rows" role="list">
      ${
        visible.length === 0
          ? html`<li class="cast__none">No character matches "${this.query()}".</li>`
          : repeat(
              visible,
              (row) => row.id,
              (row) => {
                const selected = row.id === characterId;
                const chip = readinessChip(row.readyVoiceCount, row.voiceCount);
                return html`<li>
                  <a
                    class="cast__row ${selected ? 'cast__row--selected' : ''}"
                    href=${castPath(folder, row.id)}
                    data-character-id=${row.id}
                    aria-current=${selected ? 'page' : nothing}
                  >
                    ${icon(
                      hasBookIcon(row) ? 'menu_book' : 'person',
                      `cast__icon ${hasBookIcon(row) ? 'cast__icon--book' : ''}`,
                    )}
                    <span class="cast__name">${displayName(row, narrator)}</span>
                    ${
                      row.aliases.length > 0
                        ? html`<span
                            class="cast__aliases"
                            data-tooltip=${row.aliases.map((a) => a.name).join(', ')}
                            >${row.aliases.length}</span
                          >`
                        : nothing
                    }
                    ${
                      chip
                        ? statusChip({
                            status: chip.status,
                            label: chip.label,
                            tooltip: chip.tooltip,
                            icon: 'record_voice_over',
                            compact: true,
                          })
                        : nothing
                    }
                  </a>
                </li>`;
              },
            )
      }
    </ul>`;
  }

  private detail(characterId: Guid | null) {
    const selected = this.store.selected();
    const narrator = this.narrator();
    if (selected) {
      if (selected.isNarrator && narrator?.isLinked) {
        return narratorSignpost({
          narrator,
          unusedVoices: this.narratorVoices(),
          onGoToLinked: () => this.goToLinked(),
        });
      }
      return html`<r2m-character-detail .character=${selected}></r2m-character-detail>`;
    }
    if (characterId && !this.store.loading()) {
      return emptyState(
        {
          icon: 'person_off',
          headline: 'Character not found',
          hint: 'It may have been deleted or merged.',
        },
        html`<a class="r2m-button" href=${castPath(this.folder())}>Back to the cast</a>`,
      );
    }
    return emptyState({
      icon: 'person_search',
      headline: 'Select a character',
      hint: 'Pick one on the left to see its aliases and lines.',
    });
  }

  async addCharacter(): Promise<void> {
    const name = await this.prompt.text({
      title: 'Add character',
      label: 'Name',
      required: true,
      confirmLabel: 'Add',
    });
    if (!name) return;
    // Idempotent on the host: an existing name or alias answers with that character.
    const response = await this.store.run({ type: 'CreateCharacter', name });
    if (response?.newEntityId) this.goTo(response.newEntityId);
  }

  async setNarrator(characterId: Guid | null): Promise<void> {
    await this.store.run({ type: 'SetNarratorCharacter', characterId });
  }

  goToLinked(): void {
    const id = this.narrator()?.characterId;
    if (id) this.goTo(id);
  }

  /**
   * The prompt batch (research §4): when any character already has voices, the scope dialog asks
   * whether to plan only the characters without voices or clear and replan every one. Progress
   * lives in the activity centre; the store patches cards from the batch's hub events.
   */
  async generatePrompts(): Promise<void> {
    const folder = this.store.folder();
    if (!folder || this.toolbarLocked()) return;
    const anyVoices = this.store.anyVoices();
    const request = promptBatchRequest(anyVoices, anyVoices ? await openVoiceScopeDialog() : null);
    if (!request) return;
    if (request.confirm) {
      const ok = await this.confirm.confirm({
        title: 'Clear and regenerate all voices',
        message: request.confirm,
        confirmLabel: 'Delete and regenerate',
        destructive: true,
      });
      if (!ok) return;
    }
    if (!(await this.preflight.ensureReady('voicePrompt'))) return;
    await this.startBatch(() => this.voices.startPromptBatch(folder, request.regenerateAll));
  }

  async generateAudio(): Promise<void> {
    const folder = this.store.folder();
    if (!folder || this.toolbarLocked()) return;
    if (!(await this.preflight.ensureReady('voiceDesign'))) return;
    await this.startBatch(() => this.voices.startAudioBatch(folder));
  }

  private async startBatch(start: () => Promise<unknown>): Promise<void> {
    try {
      await start();
      this.activity.drawerOpen.set(true);
    } catch (error) {
      // 409: a batch is already running — the host's message says so.
      this.toast.problem(toApiError(error).toProblem());
    }
  }

  async runDiscovery(): Promise<void> {
    const folder = this.store.folder();
    if (!folder || this.toolbarLocked()) return;
    if (!(await this.preflight.ensureReady('discovery'))) return;
    const result = await openDiscoveryDialog({ folder, roster: this.store.roster() });
    if (!result) return;
    await this.store.refresh();
    this.toast.success(
      result.applied === 1 ? '1 character added' : `${result.applied} characters added`,
    );
  }

  private async loadNarratorVoices(folder: string, narratorId: Guid): Promise<void> {
    try {
      const voices = await this.voices.list(folder, narratorId);
      this.narratorVoices.set(voices.voices.map((v) => v.name));
    } catch {
      this.narratorVoices.set([]);
    }
  }

  private goTo(id: Guid): void {
    this.router.navigate(castPath(this.folder(), id));
  }
}
define('r2m-cast-page', CastPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-cast-page': CastPage;
  }
}
