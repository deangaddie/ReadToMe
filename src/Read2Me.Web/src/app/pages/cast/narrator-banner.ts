import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { CharacterSummaryDto, Guid, NarratorDto } from '@app/api';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { StatusChip } from '@app/ui/status-chip/status-chip';

/** The unlink confirm (research §4): rendered narration will not match until regenerated. */
export function unlinkWarning(linkedCharacterName: string): string {
  return (
    `Unlink ${linkedCharacterName} as this book's narrator? Narration already rendered in ` +
    `${linkedCharacterName}'s voice will not match until it is regenerated. Existing audio is ` +
    'kept, and the Narrator voice and rules become active again.'
  );
}

/**
 * The narrator link banner above the roster (research §4): unlinked, a "Narrated by" select of
 * the non-narrator characters; linked, the name with its ready-voices chip, Change and Unlink
 * (confirmed). Emits the character to link, or null to unlink; the page posts the command.
 */
@Component({
  selector: 'app-narrator-banner',
  imports: [NgTemplateOutlet, MatButtonModule, MatFormFieldModule, MatSelectModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'narrator-banner' },
  template: `
    @if (!narrator().isLinked) {
      <div class="narrator-banner__row">
        <div class="narrator-banner__text">
          <div class="narrator-banner__title">Narrated by its own Narrator voice</div>
          <div class="narrator-banner__hint">First-person book? Say who tells it</div>
        </div>
        <ng-container *ngTemplateOutlet="picker" />
      </div>
    } @else if (changing()) {
      <div class="narrator-banner__row">
        <ng-container *ngTemplateOutlet="picker" />
        <button mat-button type="button" (click)="changing.set(false)">Cancel</button>
      </div>
    } @else {
      <div class="narrator-banner__row">
        <div class="narrator-banner__title">
          Narrated by <strong>{{ narrator().displayName }}</strong>
        </div>
        <r2m-status-chip
          [status]="ready() === 0 ? 'warn' : 'info'"
          [label]="ready() + ' ready ' + (ready() === 1 ? 'voice' : 'voices')"
          icon="record_voice_over"
          compact
        />
        <span class="narrator-banner__spacer"></span>
        <button mat-button type="button" [disabled]="busy()" (click)="changing.set(true)">
          Change
        </button>
        <button
          mat-button
          type="button"
          class="narrator-banner__unlink"
          [disabled]="busy()"
          (click)="unlink()"
        >
          Unlink
        </button>
      </div>
    }

    <ng-template #picker>
      <mat-form-field appearance="outline" class="narrator-banner__picker" subscriptSizing="dynamic">
        <mat-label>Narrated by</mat-label>
        <mat-select
          [value]="null"
          [disabled]="busy() || eligible().length === 0"
          (selectionChange)="pick($event.value)"
        >
          @for (c of eligible(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-3);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface);
    }
    .narrator-banner__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
      flex-wrap: wrap;
    }
    .narrator-banner__text {
      flex: 1 1 auto;
    }
    .narrator-banner__title {
      font-size: var(--r2m-text-md);
    }
    .narrator-banner__hint {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .narrator-banner__spacer {
      flex: 1 1 auto;
    }
    .narrator-banner__picker {
      min-width: 220px;
    }
    .narrator-banner__unlink {
      color: var(--r2m-status-error);
    }
  `,
})
export class NarratorBanner {
  private readonly confirm = inject(ConfirmService);

  readonly narrator = input.required<NarratorDto>();
  readonly rows = input.required<readonly CharacterSummaryDto[]>();
  readonly busy = input(false);

  /** A character id to link, or null to unlink. */
  readonly narratorChanged = output<Guid | null>();

  protected readonly changing = signal(false);

  /** Every character but the seed Narrator row. */
  protected readonly eligible = computed(() => this.rows().filter((r) => !r.isNarrator));

  /** The linked character's ready voices, from its roster row. */
  protected readonly ready = computed(
    () => this.rows().find((r) => r.id === this.narrator().characterId)?.readyVoiceCount ?? 0,
  );

  protected pick(id: Guid | null): void {
    if (!id) return;
    this.changing.set(false);
    this.narratorChanged.emit(id);
  }

  protected async unlink(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Unlink Narrator',
      message: unlinkWarning(this.narrator().displayName),
      confirmLabel: 'Unlink',
      destructive: true,
    });
    if (ok) this.narratorChanged.emit(null);
  }
}
