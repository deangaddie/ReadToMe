import { html, nothing } from 'lit-html';
import type { ParagraphItemDto } from '@app/api';
import { icon, statusChip } from '@app/ui/partials';
import {
  type ChipView,
  type RowContext,
  type RowPosition,
  isBusy,
  itemSpeaker,
  pauseLabel,
  queueChip,
  reviewChip,
  reviewOf,
  voiceLine,
} from './reader-rows';
import '@app/ui/speaker-chip';

/**
 * One paragraph item in Speakers or Audio mode (design §6.3), as a partial: rows render inside
 * `<r2m-measured-list>` from one effect, so there is no element per row (spec §7, risk 1).
 * Speakers: speaker chip and text, an unknown speaker highlighted. Audio: the resolved voice, voice
 * instructions, the review chip (a dismissed review is a faded icon) and the queue chip; a queued
 * item shows a lock. The chip menu, item selection, Retry/Dismiss, the player and the item menu
 * arrive with native-web 23, 25 and 26.
 */
export function itemRow(
  item: ParagraphItemDto,
  _paragraphId: string,
  ctx: RowContext,
  _position: RowPosition,
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
  const selected = ctx.itemSelectable && ctx.selectedItems.has(item.id);
  const classes = [
    'r2m-item',
    speaker.state === 'unknown' ? 'r2m-item--unknown' : '',
    selected ? 'r2m-item--selected' : '',
  ].join(' ');
  return html`<div class=${classes} data-item-id=${item.id}>
    <div class="r2m-item__line">
      <r2m-speaker-chip
        class="r2m-item__speaker"
        .compact=${true}
        .state=${speaker.state}
        .name=${speaker.name}
        .characterId=${speaker.characterId}
      ></r2m-speaker-chip>
      <span class="r2m-item__text">${item.text}</span>
      ${busy ? icon('lock', 'r2m-item__lock') : nothing}
    </div>
    ${ctx.mode === 'audio' ? audioLine(item, ctx) : nothing}
  </div>`;
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

function audioLine(item: ParagraphItemDto, ctx: RowContext) {
  const review = reviewOf(ctx, item.id);
  const reviewView = reviewChip(review);
  const queue = queueChip(ctx.itemStatus[item.id]);
  return html`<div class="r2m-item__audio">
    <span class="r2m-item__voice" data-testid="voice-line">${voiceLine(ctx.voices[item.id])}</span>
    ${
      item.voiceInstructions
        ? html`<span class="r2m-item__instructions">${item.voiceInstructions}</span>`
        : nothing
    }
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
            : chipOf(reviewView)
          : nothing
      }
      ${queue ? chipOf(queue) : nothing}
    </span>
  </div>`;
}
