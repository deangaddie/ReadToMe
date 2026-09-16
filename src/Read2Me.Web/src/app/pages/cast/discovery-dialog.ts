import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { DiscoveryApi, toApiError } from '@app/api';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { firstValueFrom } from 'rxjs';
import { AliasOwner, collides } from './alias-collisions';
import {
  DiscoveryRow,
  applyRows,
  discoveryErrorMessage,
  discoveryFailureMessage,
  rowCollisions,
  toDiscoveryRows,
  withAlias,
  withoutAlias,
} from './discovery-rows';

export interface DiscoveryDialogData {
  folder: string;
  /** The roster as it stands, for "Already exists" folding and collision detection. */
  roster: readonly AliasOwner[];
}

export interface DiscoveryDialogResult {
  applied: number;
}

export type DiscoveryPhase = 'discovering' | 'review' | 'failed';

/**
 * Discover characters (research §4 "Discover dialog"): Discovering (the LLM stream inline, Cancel)
 * → Review (editable rows: include, name, alias chips, "Already exists", collision warning) →
 * Add N selected. Re-run with the thinking toggle is the retry when the roster comes back thin;
 * Failed keeps the dialog open for exactly that. The stream group is joined for the dialog's
 * lifetime (design §9). Cancel while discovering closes the dialog; the host finishes the call
 * on its own and its answer is dropped.
 */
@Component({
  selector: 'app-discovery-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    StatusChip,
    StreamLlm,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Discover characters</h2>
    <mat-dialog-content class="discover" [attr.data-phase]="phase()">
      @if (error(); as error) {
        <p class="discover__error" role="alert">{{ error }}</p>
      }

      <div class="discover__controls">
        <mat-slide-toggle
          [checked]="thinking()"
          [disabled]="phase() === 'discovering'"
          (change)="thinking.set($event.checked)"
        >
          Enable thinking
        </mat-slide-toggle>
        <span class="discover__hint">Slower, better recall</span>
        <span class="discover__spacer"></span>
        <button
          mat-stroked-button
          type="button"
          data-action="rerun"
          [disabled]="phase() === 'discovering'"
          (click)="discover()"
        >
          <mat-icon>refresh</mat-icon> Re-run
        </button>
      </div>

      @if (phase() === 'discovering') {
        <mat-progress-bar mode="indeterminate" aria-label="Discovering" />
        <p class="discover__hint">Asking the LLM for the book's characters…</p>
      } @else if (phase() === 'review') {
        @if (collisions().length > 0) {
          <p class="discover__warning" role="alert" data-testid="collision-warning">
            Shared by more than one character: {{ collisionList() }}. Attribution resolves a name
            to a single character, so a shared one always picks the same character and never the
            others. Remove it from all but one row.
          </p>
        }
        <div class="discover__select">
          <button mat-button type="button" (click)="setAll(true)">Select all</button>
          <button mat-button type="button" (click)="setAll(false)">Select none</button>
          <span class="discover__spacer"></span>
          <span class="discover__hint">{{ includedCount() }} of {{ rows().length }} selected</span>
        </div>
        <div class="discover__rows">
          @if (rows().length === 0) {
            <p class="discover__hint">No characters found.</p>
          }
          @for (row of rows(); track $index; let i = $index) {
            <div class="discover__row" [attr.data-row]="i">
              <mat-checkbox
                [checked]="row.included"
                [aria-label]="'Include ' + row.name"
                (change)="patch(i, { included: $event.checked })"
              />
              <div class="discover__body">
                <div class="discover__name">
                  <input
                    class="discover__input"
                    type="text"
                    aria-label="Character name"
                    [value]="row.name"
                    [attr.aria-invalid]="collides(collisions(), row.name)"
                    (input)="patch(i, { name: nameOf($event) })"
                  />
                  @if (row.existingCharacterId) {
                    <r2m-status-chip status="info" label="Already exists" compact />
                  }
                </div>
                <div class="discover__aliases">
                  @for (alias of row.aliases; track alias) {
                    <span
                      class="discover__alias"
                      [class.discover__alias--collides]="collides(collisions(), alias)"
                    >
                      {{ alias }}
                      <button
                        type="button"
                        class="discover__alias-remove"
                        [attr.aria-label]="'Remove alias ' + alias"
                        (click)="removeAlias(i, alias)"
                      >
                        <mat-icon>close</mat-icon>
                      </button>
                    </span>
                  }
                  @if (addingAliasFor() === i) {
                    <input
                      class="discover__input discover__input--alias"
                      type="text"
                      placeholder="Alias name"
                      aria-label="New alias"
                      #aliasField
                      (keydown.enter)="commitAlias(i, $event)"
                      (keydown.escape)="addingAliasFor.set(null)"
                      (blur)="commitAlias(i, $event)"
                    />
                  } @else {
                    <button
                      mat-button
                      type="button"
                      class="discover__add-alias"
                      (click)="addingAliasFor.set(i)"
                    >
                      <mat-icon>add</mat-icon> Add alias
                    </button>
                  }
                </div>
              </div>
            </div>
          }
        </div>
      }

      <div class="discover__stream">
        <button mat-button type="button" (click)="showStream.set(!showStream())">
          <mat-icon>{{ showStream() ? 'keyboard_arrow_down' : 'keyboard_arrow_up' }}</mat-icon>
          {{ showStream() ? 'Hide AI activity' : 'Show AI activity' }}
        </button>
        <div class="discover__stream-body" [class.discover__stream-body--open]="showStream()">
          <r2m-stream-llm [events]="feed.events()" [maxTurns]="feed.maxUnits" />
        </div>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (phase() === 'discovering') {
        <button mat-button type="button" class="discover__cancel" (click)="ref.close()">Cancel</button>
      } @else {
        <button mat-button type="button" (click)="ref.close()">Close</button>
        <button
          mat-flat-button
          type="button"
          data-action="apply"
          [disabled]="includedCount() === 0 || applying()"
          (click)="apply()"
        >
          Add {{ includedCount() }} selected
        </button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .discover {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      width: min(720px, 90vw);
    }
    .discover p {
      margin: 0;
    }
    .discover__controls,
    .discover__select {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .discover__spacer {
      flex: 1 1 auto;
    }
    .discover__hint {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .discover__error,
    .discover__warning {
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      font-size: var(--r2m-text-sm);
      border: 1px solid var(--r2m-status-warn);
      background: var(--r2m-status-warn-soft);
    }
    .discover__error {
      border-color: var(--r2m-status-error);
      background: var(--r2m-status-error-soft);
    }
    .discover__rows {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      max-height: 50vh;
      overflow-y: auto;
    }
    .discover__row {
      display: flex;
      align-items: flex-start;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
    }
    .discover__body {
      flex: 1 1 auto;
      min-width: 0;
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
    }
    .discover__name {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .discover__input {
      font: inherit;
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-sm);
      background: var(--r2m-surface);
      color: var(--r2m-text);
      max-width: 260px;
    }
    .discover__input[aria-invalid='true'] {
      border-color: var(--r2m-status-error);
    }
    .discover__input--alias {
      max-width: 160px;
    }
    .discover__aliases {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-1);
    }
    .discover__alias {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 0 var(--r2m-space-1) 0 var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-pill);
      font-size: var(--r2m-text-sm);
    }
    .discover__alias--collides {
      border-color: var(--r2m-status-warn);
      background: var(--r2m-status-warn-soft);
    }
    .discover__alias-remove {
      display: inline-flex;
      border: 0;
      background: none;
      padding: 0;
      cursor: pointer;
      color: inherit;
    }
    .discover__alias-remove mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .discover__stream {
      border-top: 1px solid var(--r2m-outline);
      padding-top: var(--r2m-space-1);
    }
    .discover__stream-body {
      height: 0;
      overflow: hidden;
      transition: height 0.15s ease;
    }
    .discover__stream-body--open {
      height: 32vh;
    }
    .discover__cancel {
      color: var(--r2m-status-error);
    }
  `,
})
export class DiscoveryDialog implements OnDestroy {
  protected readonly data = inject<DiscoveryDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<DiscoveryDialog, DiscoveryDialogResult>>(MatDialogRef);
  protected readonly feed = inject(LlmStreamFeed);
  private readonly discovery = inject(DiscoveryApi);

  protected readonly phase = signal<DiscoveryPhase>('discovering');
  protected readonly thinking = signal(false);
  protected readonly showStream = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly rows = signal<DiscoveryRow[]>([]);
  protected readonly addingAliasFor = signal<number | null>(null);
  protected readonly applying = signal(false);
  private readonly aliasField = viewChild<ElementRef<HTMLInputElement>>('aliasField');

  protected readonly includedCount = computed(() => this.rows().filter((r) => r.included).length);
  protected readonly collisions = computed(() => rowCollisions(this.rows(), this.data.roster));
  protected readonly collisionList = computed(() =>
    [...this.collisions()].sort((a, b) => a.localeCompare(b)).join(', '),
  );
  protected readonly collides = collides;

  /** Which discovery call is current; an answer from an earlier one is dropped. */
  private run = 0;

  constructor() {
    this.feed.acquire();
    void this.discover();
    afterRenderEffect(() => {
      if (this.addingAliasFor() !== null) this.aliasField()?.nativeElement.focus();
    });
  }

  ngOnDestroy(): void {
    this.feed.release();
    this.run++;
  }

  protected async discover(): Promise<void> {
    const run = ++this.run;
    this.error.set(null);
    this.phase.set('discovering');
    try {
      const outcome = await this.discovery.discover(this.data.folder, this.thinking());
      if (run !== this.run) return;
      const failure = discoveryFailureMessage(outcome);
      if (failure) {
        // Stay open on failure: the error and Re-run are the point — a failed pass is exactly
        // when the user reaches for thinking.
        this.error.set(failure);
        this.phase.set('failed');
        return;
      }
      this.rows.set(toDiscoveryRows(outcome));
      this.phase.set('review');
    } catch (e) {
      if (run !== this.run) return;
      this.error.set(discoveryErrorMessage(toApiError(e)));
      this.phase.set('failed');
    }
  }

  protected patch(index: number, change: Partial<DiscoveryRow>): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  protected setAll(included: boolean): void {
    this.rows.update((rows) => rows.map((r) => ({ ...r, included })));
  }

  protected removeAlias(index: number, alias: string): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? withoutAlias(r, alias) : r)));
  }

  protected commitAlias(index: number, event: Event): void {
    const field = event.target as HTMLInputElement;
    const alias = field.value;
    this.rows.update((rows) => rows.map((r, i) => (i === index ? withAlias(r, alias) : r)));
    this.addingAliasFor.set(null);
  }

  protected nameOf(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected async apply(): Promise<void> {
    const rows = applyRows(this.rows());
    if (rows.length === 0) return;
    this.applying.set(true);
    try {
      const response = await this.discovery.apply(this.data.folder, rows);
      this.ref.close({ applied: response.applied });
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.applying.set(false);
    }
  }
}

/** Opens the discovery dialog; resolves how many rows were applied, or null when closed without applying. */
export async function openDiscoveryDialog(
  dialog: MatDialog,
  data: DiscoveryDialogData,
): Promise<DiscoveryDialogResult | null> {
  const ref = dialog.open<DiscoveryDialog, DiscoveryDialogData, DiscoveryDialogResult>(
    DiscoveryDialog,
    { data, autoFocus: 'dialog', restoreFocus: true },
  );
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
