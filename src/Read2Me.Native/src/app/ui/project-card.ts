import { html } from 'lit-html';
import { styleMap } from 'lit-html/directives/style-map.js';
import { type ProjectSummary, workspaceUrl } from '@app/api';
import { icon } from './partials';

/** One popover id per folder, stable across renders so the trigger and its menu always pair up. */
const menuIds = new Map<string, string>();
function menuIdFor(folder: string): string {
  let id = menuIds.get(folder);
  if (!id) menuIds.set(folder, (id = `r2m-project-card-menu-${menuIds.size + 1}`));
  return id;
}

/**
 * Shelf card (design §6.1): 3:4 cover (the project's image via `/workspace`, else generated
 * placeholder art), title, author, the audio pipeline bar or "Not read in yet", and an overflow
 * menu with Open / Delete. Clicking the cover or title calls `onOpen`; the card never navigates
 * itself. `deleting` dims the card and blocks its pointer while the delete is in flight.
 */
export function projectCard(
  project: ProjectSummary,
  options: { onOpen: () => void; onDelete: () => void; deleting?: boolean },
) {
  const coverUrl = project.coverImage ? workspaceUrl(project.folderName, project.coverImage) : null;
  const percent =
    project.audioItemTotal === 0
      ? 0
      : Math.floor((100 * project.audioItemDone) / project.audioItemTotal);
  const menuId = menuIdFor(project.folderName);
  const closeMenu = (e: Event) => {
    const menu = (e.currentTarget as HTMLElement).closest<HTMLElement>('[popover]');
    if (menu?.matches(':popover-open')) menu.hidePopover?.();
  };
  const open = (e: Event) => {
    closeMenu(e);
    options.onOpen();
  };
  const del = (e: Event) => {
    closeMenu(e);
    options.onDelete();
  };
  return html`<article
    class="r2m-project-card ${options.deleting ? 'r2m-project-card--deleting' : ''}"
    data-folder=${project.folderName}
  >
    <button
      type="button"
      class="r2m-project-card__cover"
      style=${styleMap({ background: coverUrl ? null : placeholderGradient(project.folderName) })}
      aria-label=${`Open ${project.title}`}
      @click=${options.onOpen}
    >
      ${
        coverUrl
          ? html`<img class="r2m-project-card__image" src=${coverUrl} alt=${`${project.title} cover`} />`
          : html`<span class="r2m-project-card__initials" aria-hidden="true"
              >${projectInitials(project.title)}</span
            >`
      }
    </button>

    <div class="r2m-project-card__body">
      <div class="r2m-project-card__heading">
        <button
          type="button"
          class="r2m-project-card__title"
          data-tooltip=${project.title}
          @click=${options.onOpen}
        >
          ${project.title}
        </button>
        <button
          type="button"
          class="r2m-icon-button r2m-project-card__more"
          popovertarget=${menuId}
          aria-haspopup="menu"
          aria-label=${`Actions for ${project.title}`}
        >
          ${icon('more_vert')}
        </button>
        <div id=${menuId} popover class="r2m-menu" role="menu" aria-label=${`Actions for ${project.title}`}>
          <button type="button" class="r2m-menu__item r2m-project-card__open" role="menuitem" @click=${open}>
            ${icon('open_in_new')}<span>Open</span>
          </button>
          <button
            type="button"
            class="r2m-menu__item r2m-menu__item--destructive r2m-project-card__delete"
            role="menuitem"
            @click=${del}
          >
            ${icon('delete')}<span>Delete</span>
          </button>
        </div>
      </div>
      <p class="r2m-project-card__author">${project.author || 'Unknown author'}</p>

      ${
        project.audioItemTotal > 0
          ? html`<div
              class="r2m-project-card__progress"
              role="progressbar"
              aria-valuenow=${percent}
              aria-valuemin="0"
              aria-valuemax="100"
              data-tooltip=${`Audio: ${project.audioItemDone} of ${project.audioItemTotal} items`}
            >
              <div class="r2m-project-card__bar">
                <div class="r2m-project-card__fill" style=${styleMap({ width: `${percent}%` })}></div>
              </div>
              <span class="r2m-project-card__percent">${percent}%</span>
            </div>`
          : html`<p class="r2m-project-card__unread">Not read in yet</p>`
      }
    </div>
  </article>`;
}

/**
 * First letter or digit of the first two words that have one, upper-cased; a single-word title
 * gives one letter and punctuation-only words ("(", "&") are skipped.
 */
export function projectInitials(title: string): string {
  return title
    .split(/\s+/)
    .map((w) => /\p{L}|\p{N}/u.exec(w)?.[0])
    .filter((c): c is string => c !== undefined)
    .slice(0, 2)
    .map((c) => c.toUpperCase())
    .join('');
}

/** Stable two-tone gradient from the folder name, so a project keeps its colour across reloads. */
export function placeholderGradient(seed: string): string {
  let hash = 0;
  for (const ch of seed) hash = (hash * 31 + ch.codePointAt(0)!) >>> 0;
  const hue = hash % 360;
  return `linear-gradient(135deg, hsl(${hue} 45% 48%), hsl(${(hue + 40) % 360} 50% 30%))`;
}
