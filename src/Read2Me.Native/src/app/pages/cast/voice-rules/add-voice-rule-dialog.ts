import { html, nothing } from 'lit-html';
import {
  BookApi,
  type CreateVoiceRule,
  type Guid,
  type NodeDto,
  type ParagraphDto,
  type VoiceDto,
} from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { icon } from '@app/ui/partials';
import {
  type AnchorPick,
  type AnchorSelection,
  EMPTY_ANCHOR,
  type RuleMode,
  canSubmitRule,
  ruleCommand,
  selectAnchor,
  snippet,
} from './voice-rule-logic';

export interface AddVoiceRuleDialogData {
  folder: string;
  characterId: Guid;
  /** The character's voices; the first is preselected. */
  voices: VoiceDto[];
}

/**
 * Add voice rule: a voice, the mode (From here on / Just this node) and a cascading
 * Volume → Part → Chapter → Paragraph → Line anchor. Each level is clearable and resets the deeper
 * ones; the deepest chosen level is the anchor. Add stays off until a voice and an anchor are
 * chosen. Answers the `CreateVoiceRule` command to run, or null on cancel.
 */
export class AddVoiceRuleDialog extends R2mElement {
  private readonly book = use(BookApi);

  data!: AddVoiceRuleDialogData;

  readonly voiceId = signal<Guid | null>(null);
  readonly mode = signal<RuleMode>('fromHereOn');
  readonly selection = signal<AnchorSelection>(EMPTY_ANCHOR);

  readonly volumes = signal<NodeDto[]>([]);
  readonly parts = signal<NodeDto[]>([]);
  readonly chapters = signal<NodeDto[]>([]);
  readonly paragraphs = signal<ParagraphDto[]>([]);

  readonly items = computed(() => {
    const id = this.selection().paragraphId;
    return this.paragraphs().find((p) => p.id === id)?.items ?? [];
  });
  readonly canSubmit = computed(() => canSubmitRule(this.voiceId(), this.selection()));

  protected override connected(): void {
    this.voiceId.set(this.data.voices[0]?.id ?? null);
    void this.book.overview(this.data.folder).then((o) => this.volumes.set(o.volumes));
  }

  protected template() {
    const selection = this.selection();
    const mode = this.mode();
    return html`
      <h2 class="r2m-dialog__title">Add voice rule</h2>
      <div class="r2m-dialog__content add-rule">
        <label class="r2m-field">
          <span class="r2m-field__label">Voice</span>
          <select
            data-field="voice"
            autofocus
            .value=${this.voiceId() ?? ''}
            @change=${(e: Event) => this.voiceId.set((e.target as HTMLSelectElement).value || null)}
          >
            ${this.data.voices.map(
              (v) =>
                html`<option value=${v.id} ?selected=${v.id === this.voiceId()}>${v.name}</option>`,
            )}
          </select>
        </label>

        <div class="add-rule__group">
          <span class="add-rule__label" id="rule-mode-label">Mode</span>
          <fieldset
            class="r2m-radio-group add-rule__mode"
            role="radiogroup"
            aria-labelledby="rule-mode-label"
          >
            <label class="r2m-radio">
              <input
                type="radio"
                name="mode"
                value="fromHereOn"
                .checked=${mode === 'fromHereOn'}
                @change=${() => this.mode.set('fromHereOn')}
              />
              From here on
            </label>
            <label class="r2m-radio">
              <input
                type="radio"
                name="mode"
                value="justThisNode"
                .checked=${mode === 'justThisNode'}
                @change=${() => this.mode.set('justThisNode')}
              />
              Just this node
            </label>
          </fieldset>
        </div>

        <div class="add-rule__group">
          <span class="add-rule__label">Anchor</span>
          ${this.picker('volume', 'Volume', selection.volumeId, this.volumes(), (v) => v.title || 'Untitled')}
          ${
            selection.volumeId
              ? this.picker(
                  'part',
                  'Part',
                  selection.partId,
                  this.parts(),
                  (p) => p.title || 'Untitled',
                )
              : nothing
          }
          ${
            selection.partId
              ? this.picker(
                  'chapter',
                  'Chapter',
                  selection.chapterId,
                  this.chapters(),
                  (c) => c.title || 'Untitled',
                )
              : nothing
          }
          ${
            selection.chapterId
              ? this.picker(
                  'paragraph',
                  'Paragraph',
                  selection.paragraphId,
                  this.paragraphs(),
                  (p) => this.paragraphLabel(p),
                )
              : nothing
          }
          ${
            selection.paragraphId
              ? this.picker('item', 'Line', selection.itemId, this.items(), (i) =>
                  this.itemLabel(i.text),
                )
              : nothing
          }
        </div>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', null)}>
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="add"
          ?disabled=${!this.canSubmit()}
          @click=${() => this.submit()}
        >
          Add rule
        </button>
      </div>
    `;
  }

  /** One level of the cascade: a select with a blank placeholder and a Clear button once chosen. */
  private picker<T extends { id: Guid }>(
    pick: AnchorPick,
    label: string,
    value: Guid | null,
    options: readonly T[],
    text: (option: T) => string,
  ) {
    return html`<div class="r2m-field add-rule__level">
      <label class="r2m-field__label" for=${`add-rule-${pick}`}>${label}</label>
      <div class="add-rule__control">
        <select
          id=${`add-rule-${pick}`}
          data-field=${pick}
          .value=${value ?? ''}
          @change=${(e: Event) => void this.pick(pick, (e.target as HTMLSelectElement).value || null)}
        >
          <option value="" disabled hidden ?selected=${value === null}></option>
          ${options.map(
            (o) => html`<option value=${o.id} ?selected=${o.id === value}>${text(o)}</option>`,
          )}
        </select>
        ${
          value
            ? html`<button
                type="button"
                class="r2m-icon-button add-rule__clear"
                aria-label=${`Clear ${label.toLowerCase()}`}
                @click=${() => void this.pick(pick, null)}
              >
                ${icon('close')}
              </button>`
            : nothing
        }
      </div>
    </div>`;
  }

  /** Records the pick, resets the deeper levels, and fetches the next level's options. */
  async pick(level: AnchorPick, id: Guid | null): Promise<void> {
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

  paragraphLabel(p: ParagraphDto): string {
    return snippet(p.items[0]?.text) || '(empty paragraph)';
  }

  itemLabel(text: string | null): string {
    return snippet(text) || '(pause)';
  }

  submit(): void {
    const command = ruleCommand(
      this.data.characterId,
      this.voiceId(),
      this.mode(),
      this.selection(),
    );
    if (command) this.emit<CreateVoiceRule>('r2m-close', command);
  }
}
define('r2m-add-voice-rule-dialog', AddVoiceRuleDialog);

export async function openAddVoiceRuleDialog(
  data: AddVoiceRuleDialogData,
): Promise<CreateVoiceRule | null> {
  const dialog = document.createElement('r2m-add-voice-rule-dialog');
  dialog.data = data;
  return (await openDialog<CreateVoiceRule | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-add-voice-rule-dialog': AddVoiceRuleDialog;
  }
}
