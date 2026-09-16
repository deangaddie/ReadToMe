import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CharacterSummaryDto, Guid, NarratorDto, VoicesApi } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { PromptService } from '@app/ui/text-prompt-dialog/text-prompt-dialog';
import { ToastService } from '@app/ui/toast/toast.service';
import { ProjectStore } from '../project/project-store';
import {
  CAST_SORTS,
  CAST_SORT_LABELS,
  CastSort,
  displayName,
  filterRows,
  hasBookIcon,
  readinessChip,
  sortRows,
} from './cast-rows';
import { CastStore } from './cast-store';
import { CharacterDetail } from './character-detail';
import { openDiscoveryDialog } from './discovery-dialog';
import { NarratorBanner } from './narrator-banner';
import { NarratorSignpost } from './narrator-signpost';

/**
 * `/projects/{folder}/cast[/{characterId}]` (design §6.4, ticket 15): the roster on the left —
 * search, sort, narrator banner, rows with readiness — and the selected character on the right.
 * On narrow screens the list is the page and the detail is the pushed route. The toolbar's voice
 * batches arrive with the voices slice (16); their buttons are placeholders here.
 * `?discover=1` (from the overview's pipeline) opens the discovery dialog on arrival.
 */
@Component({
  selector: 'app-cast-page',
  imports: [
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
    PageHeader,
    EmptyState,
    StatusChip,
    NarratorBanner,
    NarratorSignpost,
    CharacterDetail,
  ],
  providers: [CastStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header title="Cast" [subtitle]="project.detail()?.title" />

    @if (store.error(); as error) {
      <r2m-status-chip status="error" [label]="'Cast could not be loaded: ' + error" />
    }

    <div class="cast" [class.cast--detail]="characterId()">
      <section class="cast__list" aria-label="Characters">
        <div class="cast__toolbar">
          <button
            mat-flat-button
            type="button"
            data-action="discover"
            [disabled]="toolbarLocked()"
            (click)="startDiscovery()"
          >
            <mat-icon>auto_awesome</mat-icon> Discover
          </button>
          <button
            mat-stroked-button
            type="button"
            data-action="generate-prompts"
            disabled
            matTooltip="Arrives with the voices slice"
          >
            <mat-icon>record_voice_over</mat-icon> Generate voice prompts
          </button>
          <button
            mat-stroked-button
            type="button"
            data-action="generate-audio"
            disabled
            matTooltip="Arrives with the voices slice"
          >
            <mat-icon>graphic_eq</mat-icon> Generate audio
          </button>
          <button
            mat-stroked-button
            type="button"
            data-action="add-character"
            [disabled]="toolbarLocked()"
            (click)="addCharacter()"
          >
            <mat-icon>person_add</mat-icon> Add character
          </button>
        </div>

        @if (narrator(); as narrator) {
          <app-narrator-banner
            [narrator]="narrator"
            [rows]="store.rows()"
            [busy]="store.busy()"
            (narratorChanged)="setNarrator($event)"
          />
        }

        <div class="cast__filters">
          <mat-form-field appearance="outline" class="cast__search" subscriptSizing="dynamic">
            <mat-icon matPrefix>search</mat-icon>
            <input
              matInput
              type="search"
              placeholder="Search"
              aria-label="Search characters"
              [value]="query()"
              (input)="query.set(queryOf($event))"
            />
          </mat-form-field>
          <mat-form-field appearance="outline" class="cast__sort" subscriptSizing="dynamic">
            <mat-label>Sort</mat-label>
            <mat-select [value]="sort()" (selectionChange)="sort.set($event.value)">
              @for (s of sorts; track s) {
                <mat-option [value]="s">{{ sortLabels[s] }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>

        @if (store.loading() && store.rows().length === 0) {
          <div class="cast__skeleton" aria-busy="true" aria-label="Loading characters"></div>
        } @else if (store.rows().length === 0) {
          <r2m-empty-state
            icon="groups"
            headline="No characters yet"
            hint="Discover them with the LLM, or add one by hand."
            compact
          />
        } @else {
          <ul class="cast__rows" role="list">
            @for (row of visibleRows(); track row.id) {
              <li>
                <a
                  class="cast__row"
                  [class.cast__row--selected]="row.id === characterId()"
                  [routerLink]="['/projects', folder(), 'cast', row.id]"
                  [attr.data-character-id]="row.id"
                  [attr.aria-current]="row.id === characterId() ? 'page' : null"
                >
                  <mat-icon
                    class="cast__icon"
                    [class.cast__icon--book]="bookIcon(row)"
                    aria-hidden="true"
                  >
                    {{ bookIcon(row) ? 'menu_book' : 'person' }}
                  </mat-icon>
                  <span class="cast__name">{{ nameOf(row) }}</span>
                  @if (row.aliases.length > 0) {
                    <span
                      class="cast__aliases"
                      [matTooltip]="aliasTooltip(row)"
                      matTooltipShowDelay="400"
                    >
                      {{ row.aliases.length }}
                    </span>
                  }
                  @if (chip(row); as chip) {
                    <r2m-status-chip
                      [status]="chip.status"
                      [label]="chip.label"
                      [tooltip]="chip.tooltip"
                      icon="record_voice_over"
                      compact
                    />
                  }
                </a>
              </li>
            } @empty {
              <li class="cast__none">No character matches "{{ query() }}".</li>
            }
          </ul>
        }
      </section>

      <section class="cast__detail" aria-label="Character">
        @if (store.selected(); as selected) {
          @if (selected.isNarrator && narrator()?.isLinked) {
            <app-narrator-signpost
              [narrator]="narrator()!"
              [unusedVoices]="narratorVoices()"
              (goToLinked)="goToLinked()"
            />
          } @else {
            <app-character-detail [character]="selected" />
          }
        } @else if (characterId() && !store.loading()) {
          <r2m-empty-state
            icon="person_off"
            headline="Character not found"
            hint="It may have been deleted or merged."
          >
            <a mat-button action [routerLink]="['/projects', folder(), 'cast']">Back to the cast</a>
          </r2m-empty-state>
        } @else {
          <r2m-empty-state
            icon="person_search"
            headline="Select a character"
            hint="Pick one on the left to see its aliases and lines."
          />
        }
      </section>
    </div>
  `,
  styles: `
    .cast {
      display: grid;
      grid-template-columns: 360px minmax(0, 1fr);
      gap: var(--r2m-space-6);
      align-items: start;
      margin-top: var(--r2m-space-3);
    }
    .cast__list,
    .cast__detail {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      min-width: 0;
    }
    .cast__toolbar {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-2);
    }
    .cast__filters {
      display: flex;
      gap: var(--r2m-space-2);
    }
    .cast__search {
      flex: 1 1 auto;
    }
    .cast__sort {
      flex: 0 0 150px;
    }
    .cast__rows {
      list-style: none;
      margin: 0;
      padding: 0;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      overflow: hidden;
    }
    .cast__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      color: inherit;
      text-decoration: none;
      border-bottom: 1px solid var(--r2m-outline);
    }
    li:last-child .cast__row {
      border-bottom: 0;
    }
    .cast__row:hover {
      background: var(--r2m-surface-low);
    }
    .cast__row--selected {
      background: var(--r2m-nav-active-bg);
      color: var(--r2m-nav-active-fg);
    }
    .cast__icon {
      flex: 0 0 auto;
      font-size: 20px;
      width: 20px;
      height: 20px;
      color: var(--r2m-text-muted);
    }
    .cast__icon--book {
      color: var(--r2m-accent);
    }
    .cast__name {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }
    .cast__aliases {
      font-size: var(--r2m-text-xs);
      padding: 0 var(--r2m-space-2);
      border-radius: var(--r2m-radius-pill);
      background: var(--r2m-status-neutral-soft);
      color: var(--r2m-text-muted);
    }
    .cast__none {
      padding: var(--r2m-space-3);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .cast__skeleton {
      height: 320px;
      border-radius: var(--r2m-radius-md);
      background: color-mix(in srgb, var(--r2m-text-muted) 12%, transparent);
    }
    @media (max-width: 899.98px) {
      .cast {
        grid-template-columns: minmax(0, 1fr);
      }
      .cast--detail .cast__list,
      .cast:not(.cast--detail) .cast__detail {
        display: none;
      }
    }
  `,
})
export class CastPage {
  protected readonly store = inject(CastStore);
  protected readonly project = inject(ProjectStore);
  private readonly live = inject(LiveService);
  private readonly voices = inject(VoicesApi);
  private readonly preflight = inject(Preflight);
  private readonly prompt = inject(PromptService);
  private readonly dialog = inject(MatDialog);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Route param (`withComponentInputBinding`); absent on `/cast`. */
  readonly characterId = input<string>();
  /** `?discover=1`: open the discovery dialog on arrival, then drop the param. */
  readonly discover = input<string>();

  protected readonly sorts = CAST_SORTS;
  protected readonly sortLabels = CAST_SORT_LABELS;
  protected readonly query = signal('');
  protected readonly sort = signal<CastSort>('name');
  protected readonly narratorVoices = signal<string[]>([]);

  protected readonly folder = computed(() => this.project.folder() ?? '');
  protected readonly narrator = computed<NarratorDto | null>(
    () => this.project.detail()?.narrator ?? null,
  );
  protected readonly visibleRows = computed(() =>
    sortRows(filterRows(this.store.rows(), this.query()), this.sort()),
  );
  /** Discovery and creation are off while a write is in flight or a voice batch runs (research §4). */
  protected readonly toolbarLocked = computed(
    () => this.store.busy() || this.live.voiceBatch().isRunning,
  );

  protected readonly bookIcon = hasBookIcon;
  protected readonly chip = (row: CharacterSummaryDto) =>
    readinessChip(row.readyVoiceCount, row.voiceCount);

  constructor() {
    effect((onCleanup) => {
      const folder = this.project.folder();
      if (!folder) return;
      untracked(() => void this.store.open(folder));
      onCleanup(() => this.store.close());
    });

    // The selection follows the route, once the roster's folder is open.
    effect(() => {
      const id = this.characterId() ?? null;
      this.store.folder();
      untracked(() => void this.store.select(id));
    });

    // The Narrator signpost lists the seed row's own (unused) voices.
    effect(() => {
      const selected = this.store.selected();
      const folder = this.store.folder();
      if (!folder || !selected?.isNarrator || !this.narrator()?.isLinked) return;
      untracked(() => void this.loadNarratorVoices(folder, selected.id));
    });

    // Waits for the roster: the dialog folds rows onto it and checks collisions against it.
    effect(() => {
      if (this.discover() !== '1' || !this.store.folder() || this.store.loading()) return;
      untracked(() => {
        void this.router.navigate([], {
          relativeTo: this.route,
          queryParams: { discover: null },
          queryParamsHandling: 'merge',
          replaceUrl: true,
        });
        void this.runDiscovery();
      });
    });
  }

  protected nameOf(row: CharacterSummaryDto): string {
    return displayName(row, this.narrator());
  }

  protected aliasTooltip(row: CharacterSummaryDto): string {
    return row.aliases.map((a) => a.name).join(', ');
  }

  protected queryOf(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected async startDiscovery(): Promise<void> {
    await this.runDiscovery();
  }

  protected async addCharacter(): Promise<void> {
    const name = await this.prompt.text({
      title: 'Add character',
      label: 'Name',
      required: true,
      confirmLabel: 'Add',
    });
    if (!name) return;
    // Idempotent on the host: an existing name or alias answers with that character.
    const response = await this.store.run({ type: 'CreateCharacter', name });
    if (response?.newEntityId) await this.goTo(response.newEntityId);
  }

  protected async setNarrator(characterId: Guid | null): Promise<void> {
    await this.store.run({ type: 'SetNarratorCharacter', characterId });
  }

  protected goToLinked(): void {
    const id = this.narrator()?.characterId;
    if (id) void this.goTo(id);
  }

  private async runDiscovery(): Promise<void> {
    const folder = this.store.folder();
    if (!folder || this.toolbarLocked()) return;
    if (!(await this.preflight.ensureReady('discovery'))) return;
    const result = await openDiscoveryDialog(this.dialog, { folder, roster: this.store.roster() });
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

  private goTo(id: Guid): Promise<boolean> {
    return this.router.navigate(['/projects', this.folder(), 'cast', id]);
  }
}
