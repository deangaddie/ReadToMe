import { type TemplateResult, html, nothing } from 'lit-html';
import { ITEM_AUDIO_NOT_KEPT, type ParagraphItemDto, workspaceUrl } from '@app/api';
import { use } from '@app/core/services';
import { blockable, icon, statusChip } from '@app/ui/partials';
import { nodeMenuTrigger, runMenuEntry } from './node-menu';
import type { NodeMenuTarget } from './node-menu-entries';
import {
  type ChipView,
  type RowContext,
  type RowPosition,
  clearableOutcome,
  isBusy,
  itemSpeaker,
  pauseLabel,
  queueChip,
  reviewChip,
  reviewOf,
  voiceLine,
} from './reader-rows';
import { AudioGenerator } from './audio-generator';
import { markedText } from './pronunciation-marks';
import { ancestryFor, isVoicedItem } from './selection';
import { AudioSelectionStore } from './selection-store';
import { type AssignTarget, SpeakerAssigner } from './speaker-assigner';
import '@app/ui/audio-player';
import '@app/ui/speaker-chip';

/** Where an item sits: an assign forgets its paragraph's outcome, a ticked item rolls up into its chapter. */
export interface ItemHome {
  paragraphId: string;
  chapterId: string;
}

/**
 * One paragraph item in Speakers or Audio mode (design §6.3), as a partial: rows render inside
 * `<r2m-measured-list>` from one effect, so there is no element per row (spec §7, risk 1).
 * Speakers: speaker chip and text, an unknown speaker highlighted, and the delivery cue under
 * the text when there is one; the chip opens the speaker menu and assigns this item (ticket 12).
 * Audio: a selection checkbox (off for a line nobody can read yet, ticket 13), the resolved
 * voice, the delivery cue, the review chip (a dismissed review is a faded icon) and the queue
 * chip; a queued item shows a lock. Every item carries its node-menu trigger (ticket 11), off
 * while the item or its paragraph is queued or the editor is locked. Retry/Dismiss and the
 * player arrive with native-web 26. Like the paragraph row, the stores are reached from the
 * event's element through `use(X, element)`.
 */
export function itemRow(
  item: ParagraphItemDto,
  home: ItemHome,
  ctx: RowContext,
  position: RowPosition,
  /** The paragraph around it is queued for attribution: its items are locked too. */
  paragraphBusy = false,
) {
  if (item.isPause) {
    return html`<div class="r2m-item r2m-item--pause" data-item-id=${item.id}>
      <span class="r2m-item__pause">${pauseLabel(item)}</span>
    </div>`;
  }
  const speaker = itemSpeaker(item, ctx.speakers);
  const status = ctx.itemStatus[item.id];
  // Item queue state is audio's; attribution locks the whole paragraph instead.
  const busy = ctx.mode === 'audio' && isBusy(status);
  const menuTarget: NodeMenuTarget = {
    kind: 'item',
    id: item.id,
    text: item.text,
    cue: item.voiceInstructions,
    hasAudio: !!item.audioFileName,
    ...position,
  };
  const editingBlocked = busy || paragraphBusy || ctx.locked;
  const cue = deliveryCue(menuTarget, editingBlocked, ctx.readOnly);
  // Somebody can read the line, so it may be picked for generation (ticket 13).
  const voiced = isVoicedItem(item, ctx.narratorOnlyMode);
  const selected = ctx.itemSelectable && ctx.selectedItems.has(item.id);
  // Speakers mode only; a paragraph the queue holds is the server's to stamp, a locked book nobody's.
  const assignable =
    ctx.mode === 'speakers' && !paragraphBusy && !busy && !ctx.locked && ctx.roster.length > 0;
  const classes = [
    'r2m-item',
    speaker.state === 'unknown' ? 'r2m-item--unknown' : '',
    selected ? 'r2m-item--selected' : '',
  ].join(' ');
  const chip = speakerChipEvents({ kind: 'item', itemId: item.id, paragraphId: home.paragraphId });

  const toggle = (e: Event) => {
    const input = e.currentTarget as HTMLInputElement;
    use(AudioSelectionStore, input).toggle(
      item.id,
      ancestryFor(ctx.ancestry, home.chapterId),
      input.checked,
    );
  };

  return html`<div class=${classes} data-item-id=${item.id}>
    <div class="r2m-item__line">
      ${
        ctx.itemSelectable
          ? html`<input
              type="checkbox"
              class="r2m-item__select"
              aria-label="Select item"
              .checked=${selected}
              ?disabled=${!voiced}
              data-tooltip=${voiced ? nothing : 'Assign a speaker before generating audio'}
              @change=${toggle}
            />`
          : nothing
      }
      <r2m-speaker-chip
        class="r2m-item__speaker"
        .compact=${true}
        .state=${speaker.state}
        .name=${speaker.name}
        .characterId=${speaker.characterId}
        .roster=${assignable ? ctx.roster : undefined}
        @pick=${chip.pick}
        @clear=${chip.clear}
        @create=${chip.create}
      ></r2m-speaker-chip>
      <span class="r2m-item__text" data-item-text=${item.id}
        >${markedText(item.id, item.text ?? '', ctx.marks[item.id], { lit: ctx.litMarks })}</span
      >
      ${busy ? icon('lock', 'r2m-item__lock') : nothing}
      <span class="r2m-item__menu"
        >${nodeMenuTrigger(menuTarget, { disabled: editingBlocked, reason: ctx.readOnly })}</span
      >
    </div>
    ${
      ctx.mode === 'audio'
        ? audioLine(item, ctx, voiced, cue)
        : item.voiceInstructions
          ? html`<div class="r2m-item__cue">${cue}</div>`
          : nothing
    }
  </div>`;
}

/**
 * The speaker chip's menu events for one assign target, each resolving the {@link SpeakerAssigner}
 * the project shell provides from the element the event reached (`use(X, element)` walks DOM
 * ancestry), as the Angular rows `inject()`ed it. Shared by the paragraph and item rows.
 */
export function speakerChipEvents(target: AssignTarget) {
  const assigner = (e: Event) => use(SpeakerAssigner, e.currentTarget as Element);
  return {
    pick: (e: Event) => void assigner(e).assign(target, (e as CustomEvent<string>).detail),
    clear: (e: Event) => void assigner(e).assign(target, null),
    create: (e: Event) =>
      void assigner(e).createAndAssign(target, (e as CustomEvent<string>).detail),
  };
}

/** A compact status chip for a {@link ChipView}; the view leaves icon and tooltip out when it has none. */
export const chipOf = (view: ChipView) =>
  statusChip({
    status: view.status,
    label: view.label,
    compact: true,
    ...(view.icon ? { icon: view.icon } : {}),
    ...(view.tooltip ? { tooltip: view.tooltip } : {}),
  });

/**
 * Audio mode's second line (ticket 13): the resolved voice, the delivery cue, the review chip
 * with its Dismiss (a dismissed review is a faded icon), the queue chip with a Retry on a settled
 * failure (once somebody can read the line), and a player for the generated take (`audioVersion`
 * busts the browser cache). Retry and Dismiss reach the {@link AudioGenerator} through DOM
 * ancestry.
 */
function audioLine(item: ParagraphItemDto, ctx: RowContext, voiced: boolean, cue: DeliveryCueView) {
  const review = reviewOf(ctx, item.id);
  const reviewView = reviewChip(review);
  const status = ctx.itemStatus[item.id];
  const queue = queueChip(status);
  // A settled Failed/Unfinished take: Retry re-queues just this item.
  const outcome = clearableOutcome(status);
  const audioSrc = item.audioFileName ? workspaceUrl(ctx.folder, item.audioFileName) : null;
  const generator = (e: Event) => use(AudioGenerator, e.currentTarget as Element);
  return html`<div class="r2m-item__audio">
    <span class="r2m-item__voice" data-testid="voice-line">${voiceLine(ctx.voices[item.id])}</span>
    ${cue}
    <span class="r2m-item__chips">
      ${
        reviewView
          ? review?.state === 'Dismissed'
            ? html`<span
                class="material-symbols-rounded r2m-icon r2m-item__review-dismissed"
                data-testid="review-dismissed"
                aria-label="Review dismissed"
                data-tooltip="Review dismissed"
                >${reviewView.icon}</span
              >`
            : html`${chipOf(reviewView)}
              ${blockable(
                ctx.readOnly,
                html`<button
                  type="button"
                  class="r2m-item__action"
                  data-action="dismiss-review"
                  ?disabled=${ctx.locked}
                  @click=${(e: Event) => void generator(e).dismissReview(item.id)}
                >
                  Dismiss
                </button>`,
              )}`
          : nothing
      }
      ${queue ? chipOf(queue) : nothing}
      ${
        queue && outcome && voiced
          ? blockable(
              ctx.readOnly,
              html`<button
                type="button"
                class="r2m-item__action"
                data-action="retry-audio"
                ?disabled=${ctx.generating || ctx.locked}
                @click=${(e: Event) => void generator(e).retry(item.id)}
              >
                Retry
              </button>`,
            )
          : nothing
      }
    </span>
    ${audioSrc ? itemPlayer(audioSrc, status?.audioVersion, ctx.readOnly !== null) : nothing}
  </div>`;
}

/**
 * The generated take's player. Takes aren't Project state, so while a History step is viewed a
 * past item would play today's take: the player shows with its play button off and says why.
 */
function itemPlayer(src: string, audioVersion: number | null | undefined, viewing: boolean) {
  if (viewing) {
    return blockable(
      ITEM_AUDIO_NOT_KEPT,
      html`<r2m-audio-player .compact=${true} .src=${null}></r2m-audio-player>`,
    );
  }
  return html`<r2m-audio-player
    .compact=${true}
    .src=${src}
    .cacheKey=${audioVersion ?? undefined}
  ></r2m-audio-player>`;
}

type DeliveryCueView = TemplateResult | typeof nothing;

/**
 * An item's delivery cue (the `voiceInstructions` TTS reads the line with), one element in both
 * Speakers and Audio modes; nothing for an item without one. Read mode shows no item rows, so no
 * cue. Clicking it runs the menu's Edit delivery cue… (delivery-cue-editing 03), so it is off
 * whenever the item's menu is.
 */
function deliveryCue(
  target: NodeMenuTarget,
  disabled: boolean,
  readOnly: string | null,
): DeliveryCueView {
  const cue = target.cue;
  if (!cue) return nothing;
  const button = html`<button
    type="button"
    class="r2m-item__instructions"
    data-testid="delivery-cue"
    aria-label=${`Edit delivery cue: ${cue}`}
    ?disabled=${disabled}
    @click=${(e: Event) => void runMenuEntry('edit-cue', target, e.currentTarget as Element)}
  >
    ${cue}
  </button>`;
  return readOnly === null ? button : blockable(readOnly, button);
}
