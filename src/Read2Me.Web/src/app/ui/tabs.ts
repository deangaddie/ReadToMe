import { html, nothing } from 'lit-html';
import type { IconName } from './icons';
import { icon } from './partials';
import { rovingKeydown } from './roving';

export interface TabDef<T extends string> {
  id: T;
  label: string;
  icon?: IconName;
}

export interface TabsOptions<T extends string> {
  /** Prefix for the tab and panel ids: `${id}-tab-${tab}` and `${id}-panel-${tab}`. */
  id: string;
  tabs: readonly TabDef<T>[];
  selected: T;
  onSelect: (tab: T) => void;
  /** `aria-label` of the tablist. */
  label?: string;
}

/**
 * The `mat-tab-nav-bar` replacement (spec §5.3): a `role=tablist` of `role=tab` buttons with
 * automatic activation — arrows move focus through the shared roving helper and select as they
 * go. Render the content with {@link tabPanel} so the ids line up.
 */
export function tabs<T extends string>(options: TabsOptions<T>) {
  const { id, selected, onSelect } = options;
  const onKeydown = (event: KeyboardEvent) => {
    const list = (event.currentTarget as HTMLElement).closest<HTMLElement>('[role="tablist"]');
    if (!list) return;
    const items = Array.from(list.querySelectorAll<HTMLElement>('[role="tab"]'));
    const moved = rovingKeydown(event, items, 'horizontal');
    const tab = moved?.dataset['tab'] as T | undefined;
    if (tab !== undefined && tab !== selected) onSelect(tab);
  };
  return html`<div class="r2m-tabs" role="tablist" aria-label=${options.label ?? nothing}>
    ${options.tabs.map(
      (tab) =>
        html`<button
          type="button"
          role="tab"
          id="${id}-tab-${tab.id}"
          class="r2m-tabs__tab ${tab.id === selected ? 'r2m-tabs__tab--selected' : ''}"
          aria-selected=${tab.id === selected ? 'true' : 'false'}
          aria-controls="${id}-panel-${tab.id}"
          tabindex=${tab.id === selected ? '0' : '-1'}
          data-tab=${tab.id}
          @click=${() => onSelect(tab.id)}
          @keydown=${onKeydown}
        >
          ${tab.icon ? icon(tab.icon, 'r2m-tabs__icon') : nothing}<span class="r2m-tabs__label"
            >${tab.label}</span
          >
        </button>`,
    )}
  </div>`;
}

/** The panel for the selected tab, labelled by its tab. */
export function tabPanel(id: string, tab: string, content: unknown, cls = '') {
  return html`<div
    class="r2m-tabs__panel ${cls}"
    role="tabpanel"
    id="${id}-panel-${tab}"
    aria-labelledby="${id}-tab-${tab}"
  >
    ${content}
  </div>`;
}
