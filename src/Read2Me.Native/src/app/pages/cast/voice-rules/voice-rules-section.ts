import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { CharacterSummaryDto, VoiceRuleDto } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { type ReadonlySignal, computed, signal } from '@app/core/signals';
import { ConfirmService } from '@app/ui/dialogs';
import { icon } from '@app/ui/partials';
import { ProjectStore } from '../../project/project-store';
import { CastStore } from '../cast-store';
import { openAddVoiceRuleDialog } from './add-voice-rule-dialog';
import { describeRule, isDangling, moveAbility, showRuleControls } from './voice-rule-logic';

/**
 * The Voice rules section of the character detail (research §4 "Voice rules", ticket 17): the
 * rule rows in evaluation order with Move up/down and Delete (hidden on the default rule and on
 * the seed Narrator while the link points elsewhere), Add rule (hidden on the seed Narrator),
 * and the resolved preview — which voice wins at the start of each chapter. The section only
 * shows once the character has a rule (the first voice brings the default one). Both lists come
 * from the {@link CastStore}, which reloads them after every command and on VoiceRules / Voices /
 * Structure receipts.
 */
export class VoiceRulesSection extends R2mElement {
  private readonly confirm = use(ConfirmService);
  private store!: CastStore;
  private project!: ProjectStore;

  readonly #character = signal<CharacterSummaryDto | null>(null);
  get character() {
    return this.#character();
  }
  set character(value: CharacterSummaryDto | null) {
    this.#character.set(value);
  }

  /** The seed Narrator row while the link points at another character. */
  private linkedNarrator!: ReadonlySignal<boolean>;

  protected override connected(): void {
    this.classList.add('voice-rules-section');
    this.store = use(CastStore, this);
    this.project = use(ProjectStore, this);
    this.linkedNarrator = computed(
      () =>
        (this.#character()?.isNarrator ?? false) &&
        (this.project.detail()?.narrator.isLinked ?? false),
    );
  }

  protected template() {
    const character = this.#character();
    const rules = this.store.voiceRules();
    if (!character || rules.length === 0) return nothing;
    const busy = this.store.busy();
    const preview = this.store.voiceRulePreview();
    const linked = this.linkedNarrator();
    return html`
      <div class="voice-rules__header">
        <h3 class="voice-rules__heading">
          Voice rules <span class="voice-rules__count">${rules.length}</span>
        </h3>
        ${
          character.isNarrator
            ? nothing
            : html`<button
                type="button"
                class="r2m-button"
                data-action="add-rule"
                ?disabled=${busy || this.store.voices().voices.length === 0}
                @click=${() => void this.addRule()}
              >
                ${icon('add')} Add rule
              </button>`
        }
      </div>

      <ul class="voice-rules__list" aria-label="Voice rules">
        ${repeat(
          rules,
          (rule) => rule.ruleId,
          (rule) => this.row(rule, rules, busy, linked),
        )}
      </ul>

      ${
        preview.length > 0
          ? html`<table class="voice-rules__preview" aria-label="Resolved voice per chapter">
              <thead>
                <tr>
                  <th scope="col">Chapter</th>
                  <th scope="col">Voice</th>
                </tr>
              </thead>
              <tbody>
                ${preview.map(
                  (row) =>
                    html`<tr data-chapter-id=${row.chapterId}>
                      <td>${row.chapterTitle || 'Untitled'}</td>
                      <td class=${row.voiceName === null ? 'voice-rules__none' : ''}>
                        ${row.voiceName ?? '—'}
                      </td>
                    </tr>`,
                )}
              </tbody>
            </table>`
          : nothing
      }
    `;
  }

  private row(rule: VoiceRuleDto, rules: readonly VoiceRuleDto[], busy: boolean, linked: boolean) {
    const dangling = isDangling(rule);
    const ability = moveAbility(rules, rule.ruleId);
    return html`<li
      class="voice-rules__row ${dangling ? 'voice-rules__row--dangling' : ''}"
      data-rule-id=${rule.ruleId}
      data-default=${rule.isDefault ? 'true' : nothing}
    >
      ${
        dangling
          ? html`<span
              class="voice-rules__warning"
              role="img"
              data-tooltip="One or more anchor nodes no longer exist; this rule is skipped"
              aria-label="Missing node"
              data-role="dangling"
              >${icon('warning')}</span
            >`
          : nothing
      }
      <span class="voice-rules__text">${describeRule(rule)}</span>
      ${
        showRuleControls(rule, linked)
          ? html`<button
                type="button"
                class="r2m-icon-button"
                data-action="move-up"
                data-tooltip="Move up"
                aria-label="Move rule up"
                ?disabled=${busy || !ability.up}
                @click=${() => void this.move(rule, 'Up')}
              >
                ${icon('arrow_upward')}
              </button>
              <button
                type="button"
                class="r2m-icon-button"
                data-action="move-down"
                data-tooltip="Move down"
                aria-label="Move rule down"
                ?disabled=${busy || !ability.down}
                @click=${() => void this.move(rule, 'Down')}
              >
                ${icon('arrow_downward')}
              </button>
              <button
                type="button"
                class="r2m-icon-button voice-rules__delete"
                data-action="delete-rule"
                data-tooltip="Delete rule"
                aria-label="Delete rule"
                ?disabled=${busy}
                @click=${() => void this.deleteRule(rule)}
              >
                ${icon('delete')}
              </button>`
          : nothing
      }
    </li>`;
  }

  async addRule(): Promise<void> {
    const folder = this.store.folder();
    const character = this.#character();
    if (!folder || !character) return;
    const command = await openAddVoiceRuleDialog({
      folder,
      characterId: character.id,
      voices: this.store.voices().voices,
    });
    if (!command) return;
    await this.store.run(command);
  }

  async move(rule: VoiceRuleDto, direction: 'Up' | 'Down'): Promise<void> {
    await this.store.run({ type: 'MoveVoiceRule', ruleId: rule.ruleId, direction });
  }

  async deleteRule(rule: VoiceRuleDto): Promise<void> {
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
define('r2m-voice-rules-section', VoiceRulesSection);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-rules-section': VoiceRulesSection;
  }
}
