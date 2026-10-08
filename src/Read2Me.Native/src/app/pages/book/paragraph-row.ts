import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { ParagraphDto } from '@app/api';
import { use } from '@app/core/services';
import { speakerHue } from '@app/shared/speaker-color';
import { icon } from '@app/ui/partials';
import { chipOf, itemRow, speakerChipEvents } from './item-row';
import { nodeMenuTrigger } from './node-menu';
import type { NodeMenuTarget } from './node-menu-entries';
import {
  type RowContext,
  type RowPosition,
  clearableOutcome,
  isBusy,
  paragraphSpeaker,
  paragraphText,
  queueChip,
} from './reader-rows';
import { ancestryFor, isDialogParagraph } from './selection';
import { SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';
import '@app/ui/speaker-chip';

/**
 * One paragraph in the reader (design §6.3), as a partial — no element per row (spec §7, risk 1).
 * A gutter with the selection checkbox (Character paragraphs only, in Read and Speakers modes —
 * ticket 12) and a rail in the speaker's colour, then the body the mode chooses: prose with one
 * paragraph speaker chip in Read, one {@link itemRow} per item in Speakers and Audio. The Read
 * chip opens the speaker menu and assigns the whole paragraph. The status column shows the
 * attribution queue chip and a lock while queued (not in Audio mode) — a settled Failed/Unknown
 * chip is a button that forgets the outcome — then the paragraph's node-menu trigger (ticket 11),
 * off while the paragraph is queued or the editor is locked.
 *
 * A row holds no state of its own: the selection and the assigner are the project shell's,
 * reached from the event's element through `use(X, element)` (DOM ancestry), as the Angular row
 * reached them through `inject()`.
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
  const outcome = queue ? clearableOutcome(status) : null;
  // Queued or processing: the server would refuse an edit whatever the mode shows.
  const queued = isBusy(status);
  const busy = queue !== null && queued;
  const menuTarget: NodeMenuTarget = {
    kind: 'paragraph',
    id: paragraph.id,
    text: paragraphText(paragraph),
    ...position,
  };
  // Only Character paragraphs are selectable, and only where the selection is paragraphs.
  const selectable = ctx.selectable && isDialogParagraph(paragraph);
  const selected = ctx.selected.has(paragraph.id);
  // The Read chip assigns the whole paragraph; a queued one is the server's to stamp.
  const assignable = selectable && !queued && ctx.roster.length > 0;
  const classes = [
    'r2m-paragraph',
    `r2m-paragraph--${speaker.state}`,
    selected ? 'r2m-paragraph--selected' : '',
  ].join(' ');
  const hue = speakerHue(speaker.characterId ?? speaker.name);
  const items = paragraph.items;
  const home = { paragraphId: paragraph.id, chapterId };
  const chip = speakerChipEvents({ kind: 'paragraph', paragraphId: paragraph.id });

  const toggle = (e: Event) => {
    const input = e.currentTarget as HTMLInputElement;
    use(SelectionStore, input).toggle(
      paragraph.id,
      ancestryFor(ctx.ancestry, chapterId),
      input.checked,
    );
  };
  const clearOutcome = (e: Event) =>
    void use(SpeakerAssigner, e.currentTarget as Element).clearOutcome(paragraph.id);

  return html`<div
    class=${classes}
    data-paragraph-id=${paragraph.id}
    data-chapter-id=${chapterId}
    style="--r2m-speaker-hue: ${hue}"
  >
    <div class="r2m-paragraph__gutter">
      ${
        selectable
          ? html`<input
              type="checkbox"
              class="r2m-paragraph__select"
              aria-label="Select paragraph"
              .checked=${selected}
              @change=${toggle}
            />`
          : html`<span
              class="r2m-paragraph__select r2m-paragraph__select--none"
              aria-hidden="true"
            ></span>`
      }
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
                  .roster=${assignable ? ctx.roster : undefined}
                  @pick=${chip.pick}
                  @clear=${chip.clear}
                  @create=${chip.create}
                ></r2m-speaker-chip>
              </div>
              <p class="r2m-paragraph__prose">${paragraphText(paragraph)}</p>`
          : repeat(
              items,
              (item) => item.id,
              (item, i) =>
                itemRow(
                  item,
                  home,
                  ctx,
                  { isFirst: i === 0, isLast: i === items.length - 1 },
                  queued,
                ),
            )
      }
    </div>

    <div class="r2m-paragraph__status">
      ${
        queue && outcome
          ? html`<button
              type="button"
              class="r2m-paragraph__outcome"
              data-tooltip="${queue.tooltip ? `${queue.tooltip} — ` : ''}Click to clear"
              @click=${clearOutcome}
            >
              ${chipOf({ ...queue, tooltip: undefined })}
            </button>`
          : queue
            ? chipOf(queue)
            : nothing
      }
      ${busy ? icon('lock', 'r2m-paragraph__lock') : nothing}
      <span class="r2m-paragraph__menu"
        >${nodeMenuTrigger(menuTarget, { disabled: queued || ctx.locked })}</span
      >
    </div>
  </div>`;
}
