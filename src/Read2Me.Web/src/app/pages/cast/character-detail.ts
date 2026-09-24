import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { Router } from '@angular/router';
import { CharacterLineDto, CharacterSummaryDto, NarratorDto } from '@app/api';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { InlineEdit } from '@app/ui/inline-edit/inline-edit';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ProjectStore } from '../project/project-store';
import { CastStore } from './cast-store';
import { CharacterLines } from './character-lines';
import { openMergeDialog } from './merge-dialog';
import { VoiceRulesSection } from './voice-rules/voice-rules-section';
import { VoicesSection } from './voices/voices-section';

/** The extra note on Delete when the character narrates the book (research §4). */
export function linkedNarratorDeleteMessage(name: string): string {
  return `${name} narrates this book; deleting will return narration to the Narrator voice.`;
}

/**
 * The right-hand panel (research §4 "Detail header", "Aliases", "Lines"): rename, Merge and
 * Delete — all hidden on the seed Narrator — the alias chips, and the lines list. Every write
 * goes through the {@link CastStore}; the panel re-renders from the roster it reloads.
 */
@Component({
  selector: 'app-character-detail',
  imports: [
    MatButtonModule,
    MatIconModule,
    InlineEdit,
    StatusChip,
    CharacterLines,
    VoicesSection,
    VoiceRulesSection,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'character-detail' },
  template: `
    <header class="character-detail__header">
      @if (character().isNarrator) {
        <h2 class="character-detail__name">{{ character().name }}</h2>
      } @else {
        <r2m-inline-edit
          class="character-detail__name"
          [value]="character().name"
          placeholder="Character name"
          required
          (save)="rename($event)"
        />
        <button
          mat-stroked-button
          type="button"
          data-action="merge"
          [disabled]="store.busy() || mergeTargets() === 0"
          (click)="merge()"
        >
          <mat-icon>merge</mat-icon> Merge
        </button>
        <button
          mat-stroked-button
          type="button"
          class="character-detail__delete"
          data-action="delete"
          [disabled]="store.busy()"
          (click)="remove()"
        >
          <mat-icon>delete</mat-icon> Delete
        </button>
      }
    </header>

    @if (character().narratesBook) {
      <r2m-status-chip status="info" icon="menu_book" label="Narrates this book" compact />
    }

    @if (!character().isNarrator) {
      <section class="character-detail__section" aria-label="Aliases">
        <h3 class="character-detail__heading">Aliases</h3>
        <div class="character-detail__aliases">
          @for (alias of character().aliases; track alias.id) {
            <span class="character-detail__alias" [attr.data-alias]="alias.name">
              {{ alias.name }}
              <button
                type="button"
                class="character-detail__alias-remove"
                [attr.aria-label]="'Remove alias ' + alias.name"
                [disabled]="store.busy()"
                (click)="removeAlias(alias.id)"
              >
                <mat-icon>close</mat-icon>
              </button>
            </span>
          }
          @if (addingAlias()) {
            <input
              class="character-detail__alias-input"
              type="text"
              placeholder="Alias name"
              aria-label="New alias"
              #aliasField
              (keydown.enter)="commitAlias($event)"
              (keydown.escape)="addingAlias.set(false)"
              (blur)="commitAlias($event)"
            />
          } @else {
            <button
              mat-button
              type="button"
              data-action="add-alias"
              [disabled]="store.busy()"
              (click)="addingAlias.set(true)"
            >
              <mat-icon>add</mat-icon> Add alias
            </button>
          }
        </div>
      </section>
    }

    <section class="character-detail__section" aria-label="Voices">
      <app-voices-section [character]="character()" />
    </section>

    <section class="character-detail__section" aria-label="Voice rules">
      <app-voice-rules-section [character]="character()" />
    </section>

    <section class="character-detail__section" aria-label="Lines">
      <h3 class="character-detail__heading">
        Lines
        <span class="character-detail__count">{{ character().lineCount }}</span>
      </h3>
      <app-character-lines
        [folder]="folder()"
        [lines]="store.lines()"
        [loading]="store.linesLoading()"
        (open)="openInReader($event)"
      />
    </section>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .character-detail__header {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      flex-wrap: wrap;
    }
    .character-detail__name {
      flex: 1 1 auto;
      min-width: 0;
      margin: 0;
      font-size: var(--r2m-text-xl);
      font-weight: 500;
    }
    .character-detail__delete {
      color: var(--r2m-status-error);
    }
    .character-detail__section {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .character-detail__heading {
      margin: 0;
      font-size: var(--r2m-text-md);
      font-weight: 500;
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .character-detail__count {
      color: var(--r2m-text-muted);
      font-weight: 400;
    }
    .character-detail__aliases {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-1);
    }
    .character-detail__alias {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 2px var(--r2m-space-1) 2px var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-pill);
      font-size: var(--r2m-text-sm);
    }
    .character-detail__alias-remove {
      display: inline-flex;
      border: 0;
      background: none;
      padding: 0;
      cursor: pointer;
      color: inherit;
    }
    .character-detail__alias-remove mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .character-detail__alias-input {
      font: inherit;
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-sm);
      background: var(--r2m-surface);
      color: var(--r2m-text);
      max-width: 160px;
    }
  `,
})
export class CharacterDetail {
  protected readonly store = inject(CastStore);
  private readonly project = inject(ProjectStore);
  private readonly confirm = inject(ConfirmService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);

  readonly character = input.required<CharacterSummaryDto>();

  protected readonly addingAlias = signal(false);
  private readonly aliasField = viewChild<ElementRef<HTMLInputElement>>('aliasField');
  protected readonly folder = computed(() => this.store.folder() ?? '');
  protected readonly narrator = computed<NarratorDto | null>(
    () => this.project.detail()?.narrator ?? null,
  );
  protected readonly mergeTargets = computed(
    () => this.store.rows().filter((r) => !r.isNarrator && r.id !== this.character().id).length,
  );

  constructor() {
    afterRenderEffect(() => {
      if (this.addingAlias()) this.aliasField()?.nativeElement.focus();
    });
  }

  protected async rename(name: string): Promise<void> {
    if (name === this.character().name) return;
    await this.store.run({ type: 'RenameCharacter', characterId: this.character().id, name });
  }

  protected async removeAlias(aliasId: string): Promise<void> {
    await this.store.run({ type: 'RemoveCharacterAlias', aliasId });
  }

  protected async commitAlias(event: Event): Promise<void> {
    const name = (event.target as HTMLInputElement).value.trim();
    this.addingAlias.set(false);
    if (!name) return;
    await this.store.run({ type: 'AddCharacterAlias', characterId: this.character().id, name });
  }

  protected async merge(): Promise<void> {
    const merged = this.character();
    const choice = await openMergeDialog(this.dialog, {
      folder: this.folder(),
      merged,
      rows: this.store.rows(),
    });
    if (!choice) return;
    const response = await this.store.run({
      type: 'MergeCharacters',
      survivorId: choice.survivorId,
      mergedId: merged.id,
      addNameAsAlias: choice.addNameAsAlias,
    });
    if (response) await this.goTo(choice.survivorId);
  }

  protected async remove(): Promise<void> {
    const character = this.character();
    const note = character.narratesBook ? ` ${linkedNarratorDeleteMessage(character.name)}` : '';
    const ok = await this.confirm.confirm({
      title: 'Delete character',
      message:
        `Delete ${character.name}? Its lines become unattributed and its voices are removed.` +
        note,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!ok) return;
    const response = await this.store.run({ type: 'DeleteCharacter', characterId: character.id });
    if (response) await this.goTo(null);
  }

  /** Open the reader at the line's chapter in Speakers mode. */
  protected openInReader(line: CharacterLineDto): void {
    void this.router.navigate(['/projects', this.folder(), 'book'], {
      queryParams: { mode: 'speakers', chapter: line.chapterId },
    });
  }

  private goTo(id: string | null): Promise<boolean> {
    const base = ['/projects', this.folder(), 'cast'];
    return this.router.navigate(id ? [...base, id] : base);
  }
}
