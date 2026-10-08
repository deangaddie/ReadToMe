import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { ParagraphDto } from '@app/api';
import { speakerHue } from '@app/shared/speaker-color';
import { icon } from '@app/ui/partials';
import { chipOf, itemRow } from './item-row';
import { nodeMenuTrigger } from './node-menu';
import type { NodeMenuTarget } from './node-menu-entries';
import {
  type RowContext,
  type RowPosition,
  isBusy,
  paragraphSpeaker,
  paragraphText,
  queueChip,
} from './reader-rows';
import '@app/ui/speaker-chip';

/**
 * One paragraph in the reader (design §6.3), as a partial — no element per row (spec §7, risk 1).
 * A gutter with a rail in the speaker's colour, then the body the mode chooses: prose with one
 * paragraph speaker chip in Read, one {@link itemRow} per item in Speakers and Audio. The status
 * column shows the attribution queue chip and a lock while queued (not in Audio mode), then the
 * paragraph's node-menu trigger (ticket 11), off while the paragraph is queued or the editor is
 * locked. The selection checkbox, the chip menu and the clearable outcome arrive with native-web
 * 25; the gutter keeps the checkbox's room so rows do not shift when it lands.
 */
export function paragraphRow(
  paragraph: ParagraphDto,
  chapterId: string,
  ctx: RowContext,
  position: RowPosition,
) {
  const speaker = paragraphSpeaker(paragraph, ctx.speakers);
  const status = ctx.paragraphStatus[paragraph.id];
  // Audio mode shows the item queue instead; attribution state stays with Read and Speakers.
  const queue = ctx.mode === 'audio' ? null : queueChip(status, 'Unknown');
  // Queued or processing: the server would refuse an edit whatever the mode shows.
  const queued = isBusy(status);
  const busy = queue !== null && queued;
  const menuTarget: NodeMenuTarget = {
    kind: 'paragraph',
    id: paragraph.id,
    text: paragraphText(paragraph),
    ...position,
  };
  const selected = ctx.selected.has(paragraph.id);
  const classes = [
    'r2m-paragraph',
    `r2m-paragraph--${speaker.state}`,
    selected ? 'r2m-paragraph--selected' : '',
  ].join(' ');
  const hue = speakerHue(speaker.characterId ?? speaker.name);
  const items = paragraph.items;
  return html`<div
    class=${classes}
    data-paragraph-id=${paragraph.id}
    data-chapter-id=${chapterId}
    style="--r2m-speaker-hue: ${hue}"
  >
    <div class="r2m-paragraph__gutter">
      <span class="r2m-paragraph__select r2m-paragraph__select--none" aria-hidden="true"></span>
      <span class="r2m-paragraph__rail" aria-hidden="true"></span>
    </div>

    <div class="r2m-paragraph__body">
      ${
        ctx.mode === 'read'
          ? html`<div class="r2m-paragraph__meta">
                <r2m-speaker-chip
                  .compact=${true}
                  .state=${speaker.state}
                  .name=${speaker.name}
                  .characterId=${speaker.characterId}
                ></r2m-speaker-chip>
              </div>
              <p class="r2m-paragraph__prose">${paragraphText(paragraph)}</p>`
          : repeat(
              items,
              (item) => item.id,
              (item, i) =>
                itemRow(
                  item,
                  paragraph.id,
                  ctx,
                  { isFirst: i === 0, isLast: i === items.length - 1 },
                  queued,
                ),
            )
      }
    </div>

    <div class="r2m-paragraph__status">
      ${queue ? chipOf(queue) : nothing} ${busy ? icon('lock', 'r2m-paragraph__lock') : nothing}
      <span class="r2m-paragraph__menu"
        >${nodeMenuTrigger(menuTarget, { disabled: queued || ctx.locked })}</span
      >
    </div>
  </div>`;
}
