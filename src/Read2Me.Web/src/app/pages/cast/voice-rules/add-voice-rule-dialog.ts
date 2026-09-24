import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { BookApi, CreateVoiceRule, Guid, NodeDto, ParagraphDto, VoiceDto } from '@app/api';
import { firstValueFrom } from 'rxjs';
import {
  AnchorPick,
  AnchorSelection,
  EMPTY_ANCHOR,
  RuleMode,
  canSubmitRule,
  ruleCommand,
  selectAnchor,
  snippet,
} from './voice-rule-logic';

export interface AddVoiceRuleDialogData {
  folder: string;
  characterId: Guid;
  /** The character's voices; the first is preselected as Blazor does. */
  voices: VoiceDto[];
}

/**
 * Blazor's AddVoiceRuleDialog: a voice, the mode (From here on / Just this node) and a cascading
 * Volume → Part → Chapter → Paragraph → Line anchor. Each level is clearable and resets the deeper
 * ones; the deepest chosen level is the anchor. Add stays off until a voice and an anchor are
 * chosen. Answers the `CreateVoiceRule` command to run, or null on cancel.
 */
@Component({
  selector: 'app-add-voice-rule-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatRadioModule,
    MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Add voice rule</h2>
    <mat-dialog-content class="add-rule">
      <mat-form-field appearance="outline" subscriptSizing="dynamic">
        <mat-label>Voice</mat-label>
        <mat-select
          data-field="voice"
          [value]="voiceId()"
          (selectionChange)="voiceId.set($event.value)"
        >
          @for (v of data.voices; track v.id) {
            <mat-option [value]="v.id">{{ v.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <div class="add-rule__group">
        <span class="add-rule__label" id="rule-mode-label">Mode</span>
        <mat-radio-group
          class="add-rule__mode"
          aria-labelledby="rule-mode-label"
          [value]="mode()"
          (change)="mode.set($event.value)"
        >
          <mat-radio-button value="fromHereOn">From here on</mat-radio-button>
          <mat-radio-button value="justThisNode">Just this node</mat-radio-button>
        </mat-radio-group>
      </div>

      <div class="add-rule__group">
        <span class="add-rule__label">Anchor</span>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Volume</mat-label>
          <mat-select
            data-field="volume"
            [value]="selection().volumeId"
            (selectionChange)="pick('volume', $event.value)"
          >
            @for (v of volumes(); track v.id) {
              <mat-option [value]="v.id">{{ v.title || 'Untitled' }}</mat-option>
            }
          </mat-select>
          @if (selection().volumeId) {
            <button
              matSuffix
              mat-icon-button
              type="button"
              aria-label="Clear volume"
              (click)="pick('volume', null); $event.stopPropagation()"
            >
              <mat-icon>close</mat-icon>
            </button>
          }
        </mat-form-field>

        @if (selection().volumeId) {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Part</mat-label>
            <mat-select
              data-field="part"
              [value]="selection().partId"
              (selectionChange)="pick('part', $event.value)"
            >
              @for (p of parts(); track p.id) {
                <mat-option [value]="p.id">{{ p.title || 'Untitled' }}</mat-option>
              }
            </mat-select>
            @if (selection().partId) {
              <button
                matSuffix
                mat-icon-button
                type="button"
                aria-label="Clear part"
                (click)="pick('part', null); $event.stopPropagation()"
              >
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
        }

        @if (selection().partId) {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Chapter</mat-label>
            <mat-select
              data-field="chapter"
              [value]="selection().chapterId"
              (selectionChange)="pick('chapter', $event.value)"
            >
              @for (c of chapters(); track c.id) {
                <mat-option [value]="c.id">{{ c.title || 'Untitled' }}</mat-option>
              }
            </mat-select>
            @if (selection().chapterId) {
              <button
                matSuffix
                mat-icon-button
                type="button"
                aria-label="Clear chapter"
                (click)="pick('chapter', null); $event.stopPropagation()"
              >
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
        }

        @if (selection().chapterId) {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Paragraph</mat-label>
            <mat-select
              data-field="paragraph"
              [value]="selection().paragraphId"
              (selectionChange)="pick('paragraph', $event.value)"
            >
              @for (p of paragraphs(); track p.id) {
                <mat-option [value]="p.id">{{ paragraphLabel(p) }}</mat-option>
              }
            </mat-select>
            @if (selection().paragraphId) {
              <button
                matSuffix
                mat-icon-button
                type="button"
                aria-label="Clear paragraph"
                (click)="pick('paragraph', null); $event.stopPropagation()"
              >
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
        }

        @if (selection().paragraphId) {
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Line</mat-label>
            <mat-select
              data-field="item"
              [value]="selection().itemId"
              (selectionChange)="pick('item', $event.value)"
            >
              @for (i of items(); track i.id) {
                <mat-option [value]="i.id">{{ itemLabel(i.text) }}</mat-option>
              }
            </mat-select>
            @if (selection().itemId) {
              <button
                matSuffix
                mat-icon-button
                type="button"
                aria-label="Clear line"
                (click)="pick('item', null); $event.stopPropagation()"
              >
                <mat-icon>close</mat-icon>
              </button>
            }
          </mat-form-field>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        data-action="add"
        [disabled]="!canSubmit()"
        (click)="submit()"
      >
        Add rule
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .add-rule {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      min-width: min(420px, 85vw);
    }
    .add-rule mat-form-field {
      width: 100%;
    }
    .add-rule__group {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .add-rule__label {
      font-size: var(--r2m-text-sm);
      font-weight: 500;
      color: var(--r2m-text-muted);
    }
    .add-rule__mode {
      display: flex;
      gap: var(--r2m-space-3);
    }
  `,
})
export class AddVoiceRuleDialog {
  protected readonly data = inject<AddVoiceRuleDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<AddVoiceRuleDialog, CreateVoiceRule>>(MatDialogRef);
  private readonly book = inject(BookApi);

  protected readonly voiceId = signal<Guid | null>(this.data.voices[0]?.id ?? null);
  protected readonly mode = signal<RuleMode>('fromHereOn');
  protected readonly selection = signal<AnchorSelection>(EMPTY_ANCHOR);

  protected readonly volumes = signal<NodeDto[]>([]);
  protected readonly parts = signal<NodeDto[]>([]);
  protected readonly chapters = signal<NodeDto[]>([]);
  protected readonly paragraphs = signal<ParagraphDto[]>([]);

  protected readonly items = computed(() => {
    const id = this.selection().paragraphId;
    return this.paragraphs().find((p) => p.id === id)?.items ?? [];
  });
  protected readonly canSubmit = computed(() => canSubmitRule(this.voiceId(), this.selection()));

  constructor() {
    void this.book.overview(this.data.folder).then((o) => this.volumes.set(o.volumes));
  }

  /** Records the pick, resets the deeper levels, and fetches the next level's options. */
  protected async pick(level: AnchorPick, id: Guid | null): Promise<void> {
    this.selection.set(selectAnchor(this.selection(), level, id));
    switch (level) {
      case 'volume':
        this.parts.set([]);
        this.chapters.set([]);
        this.paragraphs.set([]);
        if (id)
          this.parts.set((await this.book.children(this.data.folder, 'volume', id)).parts ?? []);
        return;
      case 'part':
        this.chapters.set([]);
        this.paragraphs.set([]);
        if (id)
          this.chapters.set(
            (await this.book.children(this.data.folder, 'part', id)).chapters ?? [],
          );
        return;
      case 'chapter':
        this.paragraphs.set([]);
        if (id)
          this.paragraphs.set(
            (await this.book.children(this.data.folder, 'chapter', id)).paragraphs ?? [],
          );
        return;
      default:
        return;
    }
  }

  protected paragraphLabel(p: ParagraphDto): string {
    return snippet(p.items[0]?.text) || '(empty paragraph)';
  }

  protected itemLabel(text: string | null): string {
    return snippet(text) || '(pause)';
  }

  protected submit(): void {
    const command = ruleCommand(
      this.data.characterId,
      this.voiceId(),
      this.mode(),
      this.selection(),
    );
    if (command) this.ref.close(command);
  }
}

export async function openAddVoiceRuleDialog(
  dialog: MatDialog,
  data: AddVoiceRuleDialogData,
): Promise<CreateVoiceRule | null> {
  const ref = dialog.open<AddVoiceRuleDialog, AddVoiceRuleDialogData, CreateVoiceRule>(
    AddVoiceRuleDialog,
    { data, restoreFocus: true },
  );
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
