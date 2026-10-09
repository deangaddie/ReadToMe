import { html, nothing } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import type { CharacterSummaryDto, Guid, NarratorDto } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { ConfirmService } from '@app/ui/dialogs';
import { statusChip } from '@app/ui/partials';

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
 * (confirmed). Emits `narrator-changed` with the character id to link, or null to unlink; the
 * page posts the command.
 */
export class NarratorBanner extends R2mElement {
  private readonly confirm = use(ConfirmService);

  readonly #narrator = signal<NarratorDto | null>(null);
  get narrator() {
    return this.#narrator();
  }
  set narrator(value: NarratorDto | null) {
    this.#narrator.set(value);
  }

  readonly #rows = signal<readonly CharacterSummaryDto[]>([]);
  get rows() {
    return this.#rows();
  }
  set rows(value: readonly CharacterSummaryDto[]) {
    this.#rows.set(value);
  }

  readonly #busy = signal(false);
  get busy() {
    return this.#busy();
  }
  set busy(value: boolean) {
    this.#busy.set(value);
  }

  readonly changing = signal(false);

  /** Every character but the seed Narrator row. */
  private readonly eligible = computed(() => this.#rows().filter((r) => !r.isNarrator));

  /** The linked character's ready voices, from its roster row. */
  private readonly ready = computed(
    () => this.#rows().find((r) => r.id === this.#narrator()?.characterId)?.readyVoiceCount ?? 0,
  );

  protected override connected(): void {
    this.classList.add('narrator-banner');
  }

  protected template() {
    const narrator = this.#narrator();
    if (!narrator) return nothing;
    if (!narrator.isLinked) {
      return html`<div class="narrator-banner__row">
        <div class="narrator-banner__text">
          <div class="narrator-banner__title">Narrated by its own Narrator voice</div>
          <div class="narrator-banner__hint">First-person book? Say who tells it</div>
        </div>
        ${this.picker()}
      </div>`;
    }
    if (this.changing()) {
      return html`<div class="narrator-banner__row">
        ${this.picker()}
        <button type="button" class="r2m-button" @click=${() => this.changing.set(false)}>
          Cancel
        </button>
      </div>`;
    }
    const ready = this.ready();
    return html`<div class="narrator-banner__row">
      <div class="narrator-banner__title">Narrated by <strong>${narrator.displayName}</strong></div>
      ${statusChip({
        status: ready === 0 ? 'warn' : 'info',
        label: `${ready} ready ${ready === 1 ? 'voice' : 'voices'}`,
        icon: 'record_voice_over',
        compact: true,
      })}
      <span class="narrator-banner__spacer"></span>
      <button
        type="button"
        class="r2m-button"
        ?disabled=${this.#busy()}
        @click=${() => this.changing.set(true)}
      >
        Change
      </button>
      <button
        type="button"
        class="r2m-button narrator-banner__unlink"
        ?disabled=${this.#busy()}
        @click=${() => void this.unlink()}
      >
        Unlink
      </button>
    </div>`;
  }

  /** `live('')` resets the select on every render, as Angular's `[value]="null"` did, so a refused
   * link does not leave the name showing and the same name can be picked again. */
  private picker() {
    const eligible = this.eligible();
    return html`<label class="r2m-field narrator-banner__picker">
      <span class="r2m-field__label">Narrated by</span>
      <select
        .value=${live('')}
        ?disabled=${this.#busy() || eligible.length === 0}
        @change=${(e: Event) => this.pick((e.target as HTMLSelectElement).value)}
      >
        <option value="" selected disabled hidden></option>
        ${eligible.map((c) => html`<option value=${c.id}>${c.name}</option>`)}
      </select>
    </label>`;
  }

  pick(id: Guid | ''): void {
    if (!id) return;
    this.changing.set(false);
    this.emit('narrator-changed', id);
  }

  async unlink(): Promise<void> {
    const narrator = this.#narrator();
    if (!narrator) return;
    const ok = await this.confirm.confirm({
      title: 'Unlink Narrator',
      message: unlinkWarning(narrator.displayName),
      confirmLabel: 'Unlink',
      destructive: true,
    });
    if (ok) this.emit('narrator-changed', null);
  }
}
define('r2m-narrator-banner', NarratorBanner);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-narrator-banner': NarratorBanner;
  }
}
