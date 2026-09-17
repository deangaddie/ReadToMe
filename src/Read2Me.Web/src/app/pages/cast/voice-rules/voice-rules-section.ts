import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { CharacterSummaryDto, VoiceRuleDto } from '@app/api';
import { ProjectStore } from '../../project/project-store';
import { CastStore } from '../cast-store';
import { openAddVoiceRuleDialog } from './add-voice-rule-dialog';
import { describeRule, isDangling, moveAbility, showRuleControls } from './voice-rule-logic';

/**
 * The Voice rules section of the character detail (research §4 "Voice rules", ticket 17): the
 * rule rows in evaluation order with Move up/down and Delete (hidden on the default rule and on
 * the seed Narrator while the link points elsewhere), Add rule (hidden on the seed Narrator, as in
 * Blazor), and the resolved
 * preview — which voice wins at the start of each chapter. Like Blazor, the section only shows once
 * the character has a rule (the first voice brings the default one). Both lists come from the
 * {@link CastStore}, which reloads them after every command and on VoiceRules / Voices / Structure
 * receipts.
 */
@Component({
  selector: 'app-voice-rules-section',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'voice-rules-section' },
  template: `
    @if (store.voiceRules().length > 0) {
      <div class="voice-rules__header">
        <h3 class="voice-rules__heading">
          Voice rules
          <span class="voice-rules__count">{{ store.voiceRules().length }}</span>
        </h3>
        @if (!character().isNarrator) {
          <button
            mat-button
            type="button"
            data-action="add-rule"
            [disabled]="store.busy() || store.voices().voices.length === 0"
            (click)="addRule()"
          >
            <mat-icon>add</mat-icon> Add rule
          </button>
        }
      </div>

      <ul class="voice-rules__list" aria-label="Voice rules">
        @for (rule of store.voiceRules(); track rule.ruleId) {
          <li
            class="voice-rules__row"
            [class.voice-rules__row--dangling]="dangling(rule)"
            [attr.data-rule-id]="rule.ruleId"
            [attr.data-default]="rule.isDefault || null"
          >
            @if (dangling(rule)) {
              <mat-icon
                class="voice-rules__warning"
                matTooltip="One or more anchor nodes no longer exist; this rule is skipped"
                aria-label="Missing node"
                data-role="dangling"
              >
                warning
              </mat-icon>
            }
            <span class="voice-rules__text">{{ describe(rule) }}</span>
            @if (controls(rule)) {
              <button
                mat-icon-button
                type="button"
                data-action="move-up"
                matTooltip="Move up"
                aria-label="Move rule up"
                [disabled]="store.busy() || !ability(rule).up"
                (click)="move(rule, 'Up')"
              >
                <mat-icon>arrow_upward</mat-icon>
              </button>
              <button
                mat-icon-button
                type="button"
                data-action="move-down"
                matTooltip="Move down"
                aria-label="Move rule down"
                [disabled]="store.busy() || !ability(rule).down"
                (click)="move(rule, 'Down')"
              >
                <mat-icon>arrow_downward</mat-icon>
              </button>
              <button
                mat-icon-button
                type="button"
                class="voice-rules__delete"
                data-action="delete-rule"
                matTooltip="Delete rule"
                aria-label="Delete rule"
                [disabled]="store.busy()"
                (click)="remove(rule)"
              >
                <mat-icon>delete</mat-icon>
              </button>
            }
          </li>
        }
      </ul>

      @if (store.voiceRulePreview().length > 0) {
        <table class="voice-rules__preview" aria-label="Resolved voice per chapter">
          <thead>
            <tr>
              <th scope="col">Chapter</th>
              <th scope="col">Voice</th>
            </tr>
          </thead>
          <tbody>
            @for (row of store.voiceRulePreview(); track row.chapterId) {
              <tr [attr.data-chapter-id]="row.chapterId">
                <td>{{ row.chapterTitle || 'Untitled' }}</td>
                <td [class.voice-rules__none]="row.voiceName === null">
                  {{ row.voiceName ?? '—' }}
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .voice-rules__header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--r2m-space-2);
    }
    .voice-rules__heading {
      margin: 0;
      font-size: var(--r2m-text-md);
      font-weight: 500;
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .voice-rules__count {
      color: var(--r2m-text-muted);
      font-weight: 400;
    }
    .voice-rules__list {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
    }
    .voice-rules__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      min-height: 36px;
    }
    .voice-rules__text {
      flex: 1 1 auto;
      min-width: 0;
      font-size: var(--r2m-text-sm);
    }
    .voice-rules__warning {
      color: var(--r2m-status-warn);
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .voice-rules__delete {
      color: var(--r2m-status-error);
    }
    .voice-rules__preview {
      border-collapse: collapse;
      font-size: var(--r2m-text-sm);
      max-width: 480px;
    }
    .voice-rules__preview th,
    .voice-rules__preview td {
      text-align: left;
      padding: 2px var(--r2m-space-2) 2px 0;
      border-bottom: 1px solid var(--r2m-outline);
    }
    .voice-rules__preview th {
      color: var(--r2m-text-muted);
      font-weight: 500;
    }
    .voice-rules__none {
      color: var(--r2m-text-muted);
    }
  `,
})
export class VoiceRulesSection {
  protected readonly store = inject(CastStore);
  private readonly project = inject(ProjectStore);
  private readonly dialog = inject(MatDialog);
  private readonly confirm = inject(ConfirmService);

  readonly character = input.required<CharacterSummaryDto>();

  /** Blazor's IsLinkedNarrator: the seed Narrator row while the link points at another character. */
  private readonly linkedNarrator = computed(
    () => this.character().isNarrator && (this.project.detail()?.narrator.isLinked ?? false),
  );

  protected describe(rule: VoiceRuleDto): string {
    return describeRule(rule);
  }

  protected dangling(rule: VoiceRuleDto): boolean {
    return isDangling(rule);
  }

  protected controls(rule: VoiceRuleDto): boolean {
    return showRuleControls(rule, this.linkedNarrator());
  }

  protected ability(rule: VoiceRuleDto) {
    return moveAbility(this.store.voiceRules(), rule.ruleId);
  }

  protected async addRule(): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    const command = await openAddVoiceRuleDialog(this.dialog, {
      folder,
      characterId: this.character().id,
      voices: this.store.voices().voices,
    });
    if (!command) return;
    await this.store.run(command);
  }

  protected async move(rule: VoiceRuleDto, direction: 'Up' | 'Down'): Promise<void> {
    await this.store.run({ type: 'MoveVoiceRule', ruleId: rule.ruleId, direction });
  }

  protected async remove(rule: VoiceRuleDto): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Delete voice rule',
      message: `Delete the rule "${describeRule(rule)}"? The default rule takes over where it applied.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!ok) return;
    await this.store.run({ type: 'DeleteVoiceRule', ruleId: rule.ruleId });
  }
}
