import { html, nothing } from 'lit-html';
import type { NodeLevel } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { ConfirmService, confirmPermanent } from '@app/ui/dialogs';
import type { IconName } from '@app/ui/icons';
import type { MeasuredList } from '@app/ui/measured-list';
import { menuHeading, menuItem } from '@app/ui/menu';
import { adviceLine, blockable, emptyState, icon, statusChip } from '@app/ui/partials';
import type { SpeakerMenu } from '@app/ui/speaker-menu';
import { ToastService } from '@app/ui/toast';
import { rereadConfirm } from '../project/permanent-changes';
import { type StepActionId, adviceFor } from '../project/pipeline-steps';
import { ProjectStore } from '../project/project-store';
import { addTitlesCommand, addedTitlesMessage } from './add-titles';
import { openAddTitlesDialog } from './add-titles-dialog';
import { AudioGenerator } from './audio-generator';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import { type HeadingRef, type TreeNode, placeholderTitleClass } from './book-tree';
import { openEditWithAiDialog } from './edit-with-ai/edit-with-ai-dialog';
import { openManualRereadDialog } from './manual-reread-dialog';
import { type NodeMenuRequest, nodeMenuTrigger } from './node-menu';
import type { ActionEntryId, NodeMenuTarget } from './node-menu-entries';
import { paragraphRow } from './paragraph-row';
import type { PronunciationEditRequest } from './pronunciation-card';
import { PronunciationMarks } from './pronunciation-marks-store';
import type { PronunciationPanel } from './pronunciation-panel';
import { PronunciationPanelState } from './pronunciation-panel-store';
import { addPausesChange } from './permanent-changes';
import {
  READER_MODES,
  type ReaderMode,
  type ReaderRow,
  type RowContext,
  buildRows,
  parseMode,
} from './reader-rows';
import { AudioSelectionStore, SelectionStore } from './selection-store';
import { type ActionNode, SelectionWorkflow } from './selection-workflow';
import { SpeakerAssigner } from './speaker-assigner';
import { buildRoster } from './speaker-roster';
import { structureNodeMenu } from './structure-node-menu';
import '@app/ui/measured-list';
import '@app/ui/speaker-menu';
import './node-menu';
import './pronunciation-card';
import './pronunciation-panel';
import './pronunciation-pill';
import './structure-tree';
import bookCss from './book.css' with { type: 'text' };

adoptStyles(bookCss);

/** Load the adjacent chapter when the viewport is this close to either end. */
const EDGE_PX = 600;
/** How often the viewport edges are checked; see {@link BookPage.checkEdges}. */
const EDGE_POLL_MS = 250;

const MODE_LABELS: Record<ReaderMode, string> = {
  read: 'Read',
  speakers: 'Speakers',
  audio: 'Audio',
};

const SKELETON = [1, 2, 3, 4, 5, 6];
const NO_LIT: ReadonlySet<string> = new Set();
const BULK_MENU_ID = 'r2m-book-bulk-menu';
/** Bulk assign's local **Blocked reason** while the attribution queue is busy. */
const BULK_WAITS_REASON = 'Bulk assign waits until attribution has finished';
const ACTIONS_MENU_ID = 'r2m-book-actions-menu';

/**
 * `/projects/{folder}/book` (design §6.3, native-web 22–26): the structure tree beside a
 * virtual-scrolled window of adjacent chapters in `<r2m-measured-list>`. The mode (`?mode=`)
 * changes what each row shows, never where it is; `?chapter=` opens at a chapter. Rows are
 * partials rendered from the list's one effect: they read {@link ctx} there, so a live status
 * delta re-renders the visible rows and nothing else. The current chapter follows the list's top
 * row (`data-current-chapter`) and the tree, and the book reloads from live receipts through the
 * {@link BookStore}. One shared `<r2m-node-menu>` serves every tree node and row trigger.
 *
 * The paragraph selection (ticket 12) lives in Read and Speakers modes: row and tree checkboxes
 * and the tree's "Select unprocessed" over the {@link SelectionStore}, and the selection action
 * bar — Attribute (gated by preflight), Bulk assign speaker, Clear. Audio mode (ticket 13) does
 * the same for items over the {@link AudioSelectionStore}: item and tree checkboxes, "Select
 * needs audio", and a bar with Generate audio and Clear through the {@link AudioGenerator}. The
 * page forwards tree checkboxes, node shortcuts, Release narration and both bars' queue buttons
 * to the {@link SelectionWorkflow}; Clear and Bulk assign stay here. The toolbar overflow holds
 * the book-level actions (titles, pauses, Edit with AI, reread, manual reread); Edit with AI
 * (ticket 27) is `r2m-edit-with-ai-dialog`, full screen, applying through this page's
 * {@link BookEditor}.
 */
export class BookPage extends R2mElement {
  private readonly router = use(Router);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);
  private readonly confirm = use(ConfirmService);
  private project!: ProjectStore;
  private store!: BookStore;
  private editor!: BookEditor;
  private selection!: SelectionStore;
  private audioSelection!: AudioSelectionStore;
  private assigner!: SpeakerAssigner;
  private generator!: AudioGenerator;
  /** The selection intents, which selection is active, and the tree's roll-up over it. */
  private workflow!: SelectionWorkflow;
  /** The Pronunciation marks of the chapters in the reader (pronunciation ticket 15). */
  private readonly marks = new PronunciationMarks();
  /** The Pronunciations panel (pronunciation ticket 16): open or not, and what its editor holds. */
  private pronunciations!: PronunciationPanelState;
  /** The loaded items that have audio: the panel rows' with-audio counts. */
  private itemsWithAudio = computed<ReadonlySet<string>>(() => new Set());
  private readonly treeOpen = signal(true);
  /** The bulk-assign menu is open: its speaker menu is rendered fresh per open. */
  private readonly bulkOpen = signal(false);

  /** The chapter last opened from a link or the tree, until the viewport has scrolled it to the top. */
  private scrollTarget: string | null = null;
  /**
   * The chapter last opened from a link or the tree, held current until the reader scrolls: the
   * loads and anchoring that follow an open move the top row on their own, and when the chapters
   * after a short one cannot fill the viewport, the top row is never that chapter at all. Outlives
   * {@link scrollTarget}, which clears once the scroll has landed.
   */
  private openedChapter: string | null = null;
  private rows = computed<ReaderRow[]>(() => []);
  private ctx = computed<RowContext>(() => EMPTY_CONTEXT);
  private noContent = computed(() => false);
  private roster = computed(() => buildRoster([], null));
  /** Bulk assign is disarmed while attribution runs anywhere (research §3). */
  private queueBusy = computed(() => false);

  protected override connected(): void {
    this.project = use(ProjectStore, this);
    this.store = use(BookStore, this);
    this.editor = use(BookEditor, this);
    this.selection = use(SelectionStore, this);
    this.audioSelection = use(AudioSelectionStore, this);
    this.assigner = use(SpeakerAssigner, this);
    this.generator = use(AudioGenerator, this);
    this.workflow = use(SelectionWorkflow, this);
    const { project, store, editor, selection, audioSelection, generator, workflow } = this;
    const pronunciations = (this.pronunciations = new PronunciationPanelState(
      () => this.querySelector<PronunciationPanel>('r2m-pronunciation-panel')?.dirty() === true,
    ));
    this.onDisconnect(() => pronunciations.dispose());
    this.itemsWithAudio = computed(() => {
      const ids = new Set<string>();
      for (const chapter of store.chapters()) {
        for (const paragraph of chapter.paragraphs) {
          for (const item of paragraph.items) if (item.audioFileName) ids.add(item.id);
        }
      }
      return ids;
    });
    this.effect(() => {
      const folder = store.folder();
      if (folder) untracked(() => pronunciations.follow(folder));
    });
    // The panel is off while a step is viewed.
    this.effect(() => {
      if (project.readOnly() !== null) untracked(() => pronunciations.hide());
    });

    this.noContent = computed(() => store.overview()?.hasContent === false);
    this.rows = computed(() => buildRows(store.chapters(), store.mode()));
    this.roster = computed(() =>
      buildRoster(store.overview()?.characters ?? [], project.detail()?.narrator ?? null),
    );
    this.queueBusy = computed(() => this.live.queue()?.attribution.isBusy ?? false);

    this.ctx = computed<RowContext>(() => ({
      folder: store.folder() ?? '',
      mode: store.mode(),
      speakers: store.speakers(),
      paragraphStatus: project.paragraphs(),
      itemStatus: project.items(),
      voices: store.itemVoices(),
      reviews: store.reviews(),
      selectable: workflow.kind() === 'paragraphs',
      selected: selection.selected(),
      itemSelectable: workflow.kind() === 'items',
      selectedItems: audioSelection.selected(),
      narratorOnlyMode: workflow.narratorOnlyMode(),
      ancestry: store.ancestry(),
      roster: this.roster(),
      blocked: editor.blocked(),
      viewing: project.readOnly() !== null,
      generating: generator.working(),
      marks: this.marks.marks().byItem,
      litMarks: pronunciations.open() ? pronunciations.lit() : NO_LIT,
    }));

    // The reader's chapters, read (again): their Pronunciation marks follow, at the viewed step if any.
    this.effect(() => {
      const folder = store.folder();
      const chapterIds = store.chapters().map((c) => c.id);
      // Viewing a step (or going back to current) rereads the marks at that step.
      project.readOnly();
      if (folder) untracked(() => this.marks.follow(folder, chapterIds));
    });
    this.onDisconnect(() => this.marks.close());

    this.effect(() => {
      const mode = parseMode(this.router.query().get('mode'));
      untracked(() => store.setMode(mode));
    });

    this.effect(() => {
      const folder = project.folder();
      if (!folder) return;
      untracked(() => void this.start(folder));
    });

    // `?action=` (from the Overview's rows): once the book and its Pipeline steps have loaded (the
    // dialog shows their advice) and nothing locks the editor, drop the param and open what the
    // overflow entry opens. Unknown, no content, or a viewed History step: drop only, so the
    // dialog doesn't open by itself on Back to current.
    this.effect(() => {
      const action = this.router.query().get('action');
      const overview = store.overview();
      if (action === null || !overview || !project.pipeline()) return;
      const viewing = project.readOnly() !== null;
      if (editor.blocked() !== null && !viewing) return;
      untracked(() => {
        const query = Object.fromEntries(this.router.query());
        this.router.navigate(this.router.path(), {
          replace: true,
          query: { ...query, action: null },
        });
        const open = overview.hasContent && !viewing ? this.deepLinks.get(action) : undefined;
        if (open) void open();
      });
    });

    this.effect(() => {
      const request = store.scrollRequest();
      if (!request) return;
      untracked(() => this.scrollToChapter(request.chapterId));
    });

    // The tree (native-web 23) reads the store; E2E reads this attribute.
    this.effect(() => {
      const current = store.currentChapterId();
      if (current) this.setAttribute('data-current-chapter', current);
      else this.removeAttribute('data-current-chapter');
    });

    const edges = setInterval(() => this.checkEdges(), EDGE_POLL_MS);
    this.onDisconnect(() => clearInterval(edges));
  }

  protected template() {
    const store = this.store;
    const rows = this.rows();
    const stale = store.stale();
    const treeOpen = this.treeOpen();
    const noContent = this.noContent();
    return html`
      <div class="book__toolbar" role="toolbar" aria-label="Reader">
        <button
          type="button"
          class="r2m-icon-button"
          aria-label=${treeOpen ? 'Hide structure' : 'Show structure'}
          aria-expanded=${treeOpen ? 'true' : 'false'}
          @click=${() => this.treeOpen.set(!treeOpen)}
        >
          ${icon(treeOpen ? 'left_panel_close' : 'left_panel_open')}
        </button>
        <fieldset class="r2m-segmented" role="radiogroup" aria-label="Reader mode">
          ${READER_MODES.map(
            (m) =>
              html`<label>
                <input
                  type="radio"
                  name="reader-mode"
                  .value=${m}
                  .checked=${store.mode() === m}
                  @change=${() => this.setMode(m)}
                />
                ${MODE_LABELS[m]}
              </label>`,
          )}
        </fieldset>
        ${noContent ? nothing : this.pronunciationsToggle()}
        ${
          this.workflow.active().count() === 0
            ? nothing
            : this.workflow.kind() === 'items'
              ? this.audioSelectionBar()
              : this.selectionBar()
        }
        <span class="book__spacer"></span>
        ${
          this.editor.saving()
            ? html`<span data-testid="book-saving"
                >${statusChip({ status: 'busy', label: 'Saving…', compact: true })}</span
              >`
            : nothing
        }
        ${noContent ? nothing : this.bookActions()}
      </div>

      ${
        stale
          ? html`<div class="book__stale" role="alert">
              ${icon('sync_problem')}
              <span>This view may be out of date: ${stale}</span>
              <button type="button" class="r2m-button" @click=${this.refresh}>Refresh</button>
            </div>`
          : nothing
      }
      ${
        noContent
          ? emptyState(
              {
                icon: 'menu_book',
                headline: 'The book has not been read in yet',
                hint: 'Read it in from the project overview.',
              },
              [{ label: 'Go to overview', href: './' }],
            )
          : html`<div
              class="book__body ${treeOpen ? '' : 'book__body--tree-hidden'} ${
                this.pronunciations.open() ? 'book__body--pron' : ''
              }"
            >
              ${
                treeOpen
                  ? html`<nav class="book__tree" aria-label="Structure">
                      <r2m-structure-tree
                        .nodes=${store.tree()}
                        .statuses=${this.project.nodes()}
                        .currentChapterId=${store.currentChapterId()}
                        .expandedIds=${store.expanded()}
                        .selection=${this.workflow.kind()}
                        .nodeStates=${this.workflow.nodeStates()}
                        .blocked=${this.editor.blocked()}
                        @expanded-change=${this.onExpandedChange}
                        @select-chapter=${this.onSelectChapter}
                        @toggle-node=${this.onToggleNode}
                        @node-action=${this.onNodeAction}
                      ></r2m-structure-tree>
                    </nav>`
                  : nothing
              }
              <section
                class="book__reader"
                aria-label="Chapter"
                @r2m-node-menu-open=${this.onRowMenuOpen}
              >
                ${
                  rows.length === 0
                    ? html`<div
                        class="book__skeleton"
                        aria-busy="true"
                        aria-label="Loading chapter"
                      >
                        ${SKELETON.map(() => html`<div class="book__skeleton-line"></div>`)}
                      </div>`
                    : nothing
                }
                <r2m-measured-list
                  class="book__viewport ${rows.length === 0 ? 'book__viewport--empty' : ''}"
                  .key=${rowKey}
                  .row=${this.renderRow}
                  .items=${rows}
                  @r2m-top-row=${this.onTopRow}
                  @r2m-reader-scroll=${this.onReaderScroll}
                ></r2m-measured-list>
              </section>
              <r2m-pronunciation-panel
                class="book__pron"
                .state=${this.pronunciations}
                .folder=${store.folder()}
                .marks=${this.marks.marks()}
                .itemsWithAudio=${this.itemsWithAudio()}
                .readOnly=${this.project.readOnly()}
                .audioInFlight=${this.project.audioInFlight()}
              ></r2m-pronunciation-panel>
            </div>`
      }
      <r2m-node-menu></r2m-node-menu>
      <r2m-pronunciation-card
        .folder=${store.folder()}
        .marks=${this.marks.marks()}
        .readOnly=${this.project.readOnly()}
        @pronunciation-edit=${this.onPronunciationEdit}
      ></r2m-pronunciation-card>
      <r2m-pronunciation-pill
        .state=${this.pronunciations}
        .readOnly=${this.project.readOnly()}
      ></r2m-pronunciation-pill>
    `;
  }

  /** The toolbar's Pronunciations toggle (pronunciation ticket 16): off while a step is viewed. */
  private pronunciationsToggle() {
    const open = this.pronunciations.open();
    const readOnly = this.project.readOnly();
    return blockable(
      readOnly,
      html`<button
        type="button"
        class="r2m-button book__pron-toggle ${open ? 'book__pron-toggle--on' : ''}"
        data-action="toggle-pronunciations"
        aria-pressed=${open ? 'true' : 'false'}
        ?disabled=${readOnly !== null}
        @click=${() => this.pronunciations.toggle()}
      >
        ${icon('text_to_speech')} Pronunciations
      </button>`,
    );
  }

  /** A mark's Edit pencil: the entry opens in the panel, its try-out the mark's sentence. */
  private readonly onPronunciationEdit = (e: Event): void => {
    const { pronunciationId, place } = (e as CustomEvent<PronunciationEditRequest>).detail;
    void this.pronunciations.edit(pronunciationId, place);
  };

  /** The paragraph selection's action bar (ticket 12): count, Attribute, Bulk assign speaker, Clear. */
  private selectionBar() {
    const count = this.selection.count();
    const attribute = this.editor.blocked(this.workflow.working());
    // A busy queue gives Bulk assign its own reason; viewing and stale still win.
    const bulk = this.editor.blocked(
      this.queueBusy() ? BULK_WAITS_REASON : this.workflow.working(),
    );
    return html`<div
      class="book__selection"
      role="group"
      aria-label="Selection"
      data-testid="selection-bar"
    >
      <span class="book__selection-count" data-testid="selection-count"
        >${count} paragraph${count === 1 ? '' : 's'}</span
      >
      ${blockable(
        attribute?.reason ?? null,
        html`<button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="attribute-selection"
          ?disabled=${attribute !== null}
          @click=${() => void this.workflow.attribute()}
        >
          ${icon('auto_awesome')} Attribute
        </button>`,
      )}
      <span class="book__bulk">
        ${blockable(
          bulk?.reason ?? null,
          html`<button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="bulk-assign"
            popovertarget=${BULK_MENU_ID}
            aria-haspopup="listbox"
            ?disabled=${bulk !== null}
          >
            ${icon('groups')} Bulk assign speaker ${icon('expand_more')}
          </button>`,
        )}
        <div
          id=${BULK_MENU_ID}
          popover
          class="r2m-menu book__bulk-menu"
          @toggle=${this.onBulkToggle}
          @pick=${(e: Event) => this.bulkAssign((e as CustomEvent<string>).detail)}
          @clear=${() => this.bulkAssign(null)}
          @create=${(e: Event) => this.bulkCreate((e as CustomEvent<string>).detail)}
        >
          ${
            this.bulkOpen()
              ? html`<r2m-speaker-menu .roster=${this.roster()}></r2m-speaker-menu>`
              : nothing
          }
        </div>
      </span>
      <button
        type="button"
        class="r2m-button"
        data-action="clear-selection"
        @click=${() => this.selection.clear()}
      >
        Clear
      </button>
    </div>`;
  }

  /** The item selection's action bar (ticket 13): count, Generate audio, Clear. */
  private audioSelectionBar() {
    const count = this.audioSelection.count();
    const generate = this.editor.blocked(this.workflow.working() || this.generator.working());
    return html`<div
      class="book__selection"
      role="group"
      aria-label="Selection"
      data-testid="selection-bar"
    >
      <span class="book__selection-count" data-testid="selection-count"
        >${count} item${count === 1 ? '' : 's'}</span
      >
      ${blockable(
        generate?.reason ?? null,
        html`<button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="generate-audio-selection"
          ?disabled=${generate !== null}
          @click=${() => void this.workflow.generate()}
        >
          ${icon('graphic_eq')} Generate audio
        </button>`,
      )}
      ${adviceLine(this.advice('generateAudio'))}
      <button
        type="button"
        class="r2m-button"
        data-action="clear-selection"
        @click=${() => this.audioSelection.clear()}
      >
        Clear
      </button>
    </div>`;
  }

  /**
   * The toolbar overflow (ticket 11), grouped under Pipeline headings (guidance-primitives spec §5):
   * the book-level commands, each posting through the editor. An entry's advice is its second line.
   */
  private bookActions() {
    const entry = (
      action: string,
      label: string,
      glyph: IconName,
      run: () => void,
      hint: string | null = null,
    ) => menuItem({ action, label, icon: glyph, run, ...(hint === null ? {} : { hint }) });
    const block = this.editor.blocked(!this.store.overview());
    return html`${blockable(
        block?.reason ?? null,
        html`<button
          type="button"
          class="r2m-icon-button"
          aria-label="Book actions"
          aria-haspopup="menu"
          data-testid="book-actions"
          popovertarget=${ACTIONS_MENU_ID}
          ?disabled=${block !== null}
        >
          ${icon('more_vert')}
        </button>`,
      )}
      <div id=${ACTIONS_MENU_ID} popover class="r2m-menu" role="menu" aria-label="Book actions">
        ${menuHeading('Structure')}
        ${entry('reread', 'Reread… (auto splits)', 'restart_alt', () => void this.reread())}
        ${entry('manual-reread', 'Reread with split rules…', 'tune', () => void this.rereadManually())}
        <hr class="r2m-menu__divider" />
        ${menuHeading('Text')}
        ${entry('edit-with-ai', 'Edit with AI…', 'auto_fix_high', () => void this.editWithAi())}
        <hr class="r2m-menu__divider" />
        ${menuHeading('Titles and pauses')}
        ${entry(
          'add-titles',
          'Add titles…',
          'title',
          () => void this.addTitles(),
          this.advice('addTitles'),
        )}
        ${entry(
          'add-pauses',
          'Add pauses',
          'pause',
          () => void this.addPauses(),
          this.advice('addPauses'),
        )}
      </div>`;
  }

  private readonly onExpandedChange = (e: Event): void => {
    const { node, expanded } = (e as CustomEvent<{ node: TreeNode; expanded: boolean }>).detail;
    this.store.setExpanded(node, expanded);
  };

  /** A tree pick opens the chapter and names it in the URL, so a reload or a shared link keeps it. */
  private readonly onSelectChapter = (e: Event): void => {
    const chapterId = (e as CustomEvent<string>).detail;
    void this.store.openChapter(chapterId);
    const query = Object.fromEntries(this.router.query());
    this.router.navigate(this.router.path(), {
      replace: true,
      query: { ...query, chapter: chapterId },
    });
  };

  /** Reads {@link ctx} inside the list's effect, so status deltas re-render only the rows. */
  private readonly renderRow = (row: ReaderRow) => {
    switch (row.kind) {
      case 'volume':
      case 'part':
        return this.headingRow(row.kind, row);
      case 'chapter':
        return this.headingRow('chapter', { ...row, id: row.chapterId });
      case 'paragraph':
        return html`<div class="book__row">
          ${paragraphRow(row.paragraph, row.chapterId, this.ctx(), row)}
        </div>`;
      case 'pause': {
        const target: NodeMenuTarget = {
          kind: 'pause-paragraph',
          id: row.paragraph.id,
          text: row.label,
          isFirst: row.isFirst,
          isLast: row.isLast,
        };
        return html`<div class="book__pause book__row" role="separator" aria-label=${row.label}>
          <span class="book__pause-label">${row.label}</span>
          <span class="book__pause-rule"></span>
          <span class="book__pause-menu"
            >${nodeMenuTrigger(target, { block: this.ctx().blocked })}</span
          >
        </div>`;
      }
    }
  };

  /**
   * A Volume, Part or Chapter heading with the node's Node menu trigger at the end of the row,
   * offering what the tree offers for it (structureNodeMenu).
   */
  private headingRow(level: NodeLevel, heading: HeadingRef) {
    const { id } = heading;
    const menu = structureNodeMenu({
      node: { level, id, rawTitle: heading.rawTitle },
      position: heading,
      selection: this.workflow.kind(),
      status: this.project.nodes()[id] ?? null,
      blocked: this.ctx().blocked,
    });
    return html`<div class="book__heading-row book__row">
      <div
        class="book__heading"
        data-chapter-id=${level === 'chapter' ? id : nothing}
        data-node-id=${level === 'chapter' ? nothing : id}
      >
        <h2 class="book__${level} ${placeholderTitleClass(heading)}">${heading.title}</h2>
        <span class="book__heading-menu"
          >${nodeMenuTrigger(menu.target, { block: menu.block })}</span
        >
      </div>
    </div>`;
  }

  private setMode(mode: ReaderMode): void {
    this.store.setMode(mode);
    const query = Object.fromEntries(this.router.query());
    this.router.navigate(this.router.path(), {
      replace: true,
      query: { ...query, mode: mode === 'read' ? null : mode },
    });
  }

  private readonly onTopRow = (e: Event): void => {
    if (this.openedChapter) return;
    const row = this.rows()[(e as CustomEvent<number>).detail];
    if (row) this.store.setCurrentChapter(row.chapterId);
  };

  /** The reader took over from an open: the chapter at the top is current again. */
  private readonly onReaderScroll = (e: Event): void => {
    this.openedChapter = null;
    this.onTopRow(e);
  };

  private readonly refresh = (): void => {
    void this.store.refresh();
  };

  // ---- selection (tickets 12 and 13) ----------------------------------------------------------------

  /** A tree checkbox: the workflow reads what the node holds into the mode's selection. */
  private readonly onToggleNode = (e: Event): void => {
    const { node, on } = (e as CustomEvent<{ node: TreeNode; on: boolean }>).detail;
    void this.workflow.toggleNode(node, on);
  };

  /** The tree's node menu shortcuts; a Book page heading's arrive through {@link onRowMenuOpen}. */
  private readonly onNodeAction = (e: Event): void => {
    const { node, action } = (e as CustomEvent<{ node: TreeNode; action: ActionEntryId }>).detail;
    void this.workflow.nodeAction(node, action);
  };

  /**
   * A reader row's menu opening (`NODE_MENU_OPEN`, on its way to the shared menu): rows have no
   * tree to take their actions, so the reader does. A Volume, Part or Chapter heading's go where
   * the tree's do; a paragraph's only one is a narration-only paragraph's Release narration.
   */
  private readonly onRowMenuOpen = (e: Event): void => {
    const request = (e as CustomEvent<NodeMenuRequest>).detail;
    const { target } = request;
    if (target.kind === 'volume' || target.kind === 'part' || target.kind === 'chapter') {
      const level = target.kind;
      const heading = this.store
        .chapters()
        .map((c) => (level === 'chapter' ? c : c[level]))
        .find((h) => h?.id === target.id);
      if (!heading) return;
      const node: ActionNode = { level, id: heading.id, title: heading.title };
      request.onAction = (action) => void this.workflow.nodeAction(node, action);
      return;
    }
    if (target.kind !== 'paragraph') return;
    request.onAction = (action) => {
      if (action === 'release-narration') {
        void this.workflow.releaseParagraph(target.id, target.text ?? '');
      }
    };
  };

  /** A pick, clear or create closes the menu, like a mat-menu item click. */
  private readonly onBulkToggle = (e: ToggleEvent): void => {
    const open = e.newState === 'open';
    this.bulkOpen.set(open);
    // Mouse or trackpad: type to search straight away, as the chip's menu does. Touch skips it.
    if (open && matchMedia('(pointer: fine)').matches) {
      void this.rendered().then(() =>
        this.querySelector<SpeakerMenu>('.book__bulk-menu r2m-speaker-menu')?.focusSearch(),
      );
    }
  };

  /** Top-layer calls are optional: happy-dom has no popover API (web.md, conventions 11). */
  private closeMenu(id: string): void {
    const menu = this.querySelector<HTMLElement>(`#${id}`);
    if (menu?.matches(':popover-open')) menu.hidePopover?.();
  }

  private bulkAssign(characterId: string | null): void {
    this.closeMenu(BULK_MENU_ID);
    void this.assigner.assign(
      { kind: 'selection', paragraphIds: this.selection.ids() },
      characterId,
    );
  }

  private bulkCreate(name: string): void {
    this.closeMenu(BULK_MENU_ID);
    void this.assigner.createAndAssign(
      { kind: 'selection', paragraphIds: this.selection.ids() },
      name,
    );
  }

  // ---- book actions (ticket 11) -------------------------------------------------------------------

  /** What `?action=` opens, by query value: the same as the overflow entry (spec §5). */
  private readonly deepLinks: ReadonlyMap<string, () => Promise<void>> = new Map([
    ['titles', () => this.addTitles()],
    ['edit-ai', () => this.editWithAi()],
    ['manual-reread', () => this.rereadManually()],
  ]);

  /** An action's advice line (spec §2) over the project's Pipeline steps, or null. */
  private advice(action: StepActionId): string | null {
    return adviceFor(action, this.project.pipeline() ?? []);
  }

  /**
   * Add titles (guidance-primitives spec §5): the dialog is its own confirm. One request for every
   * ticked level; a refusal is the editor's toast, which names the levels that had already landed.
   */
  private async addTitles(): Promise<void> {
    const overview = this.store.overview();
    if (!overview) return;
    const levels = await openAddTitlesDialog({
      volumes: overview.volumes.length,
      parts: overview.totalParts,
      chapters: overview.totalChapters,
      advice: this.advice('addTitles'),
    });
    if (!levels) return;
    if (await this.editor.run(addTitlesCommand(levels))) {
      this.toast.success(addedTitlesMessage(levels));
    }
  }

  /** Add pauses always asks (guidance-primitives spec §7): each pause is a delete to undo. */
  private async addPauses(): Promise<void> {
    const chapters = this.store.overview()?.totalChapters ?? 0;
    if (!(await confirmPermanent(addPausesChange(chapters)))) return;
    await this.editor.run({ type: 'AddPauses' });
  }

  private async reread(): Promise<void> {
    const ok = await this.confirm.confirm(rereadConfirm(this.project.status()?.checkpoints ?? {}));
    if (ok) await this.editor.reread();
  }

  private async rereadManually(): Promise<void> {
    const request = await openManualRereadDialog();
    if (request) await this.editor.rereadManually(request);
  }

  /**
   * Edit with AI. The dialog applies through {@link BookEditor} itself, so the reader is already
   * showing the result when it closes; this only reports what landed.
   */
  private async editWithAi(): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    const result = await openEditWithAiDialog(folder, this.editor);
    if (result) {
      this.toast.success(`Applied ${result.applied} edit${result.applied === 1 ? '' : 's'}.`);
    }
  }

  // ---- the window ---------------------------------------------------------------------------------

  private list(): MeasuredList<ReaderRow> | null {
    return this.querySelector<MeasuredList<ReaderRow>>('r2m-measured-list');
  }

  private async start(folder: string): Promise<void> {
    this.openedChapter = null; // another project's chapter
    await this.store.open(folder);
    if (this.noContent()) return;
    // `?chapter=` wins over whatever the window holds: a reload may have opened the first chapter
    // while the overview was still loading.
    const chapter = this.router.query().get('chapter');
    if (chapter && !this.store.window().includes(chapter)) await this.store.openChapter(chapter);
    else if (this.store.window().length === 0) await this.store.openFirstChapter();
  }

  private scrollToChapter(chapterId: string): void {
    void this.rendered().then(() => {
      const index = this.chapterRowIndex(chapterId);
      const list = this.list();
      if (index < 0 || !list) return;
      this.scrollTarget = chapterId;
      this.openedChapter = chapterId;
      list.scrollToIndex(index);
      // The chapter before is needed to scroll up from the top of this one; the edge check loads
      // it once that can keep this chapter in place.
      this.checkEdges();
    });
  }

  /**
   * Loads the neighbouring chapter when the viewport nears an end. Polled rather than tied to
   * scroll events: a chapter shorter than the viewport never scrolls, and wheel input at the very
   * top fires no scroll event at all.
   *
   * Two rules keep the chapter the reader is on in place when chapters are short (a part whose
   * only chapter is its heading). Content shorter than the viewport cannot scroll, so a chapter
   * prepended to it pushes the reader's chapter down: load next until there is something to
   * scroll, and previous only then (or when there is no next). And a full window drops its far
   * end only when that chapter is off screen by a margin; dropping one still in view would empty
   * the screen and start the loads over. A chapter opened from a link that could not scroll to
   * the top yet ({@link scrollTarget}) is scrolled there once the chapters after it have loaded.
   */
  private checkEdges(): void {
    const list = this.list();
    if (!list || this.rows().length === 0) return;
    const top = list.scrollOffset('top');
    const bottom = list.scrollOffset('bottom');
    const size = list.clientHeight;
    const loaded = this.store.window();
    const scrollable = top + bottom >= 1;

    const reaching = this.reachScrollTarget(list, top, bottom);
    if (bottom < EDGE_PX) {
      // Dropping the first chapter is safe when the second starts well above the viewport.
      const second = loaded[1];
      void this.store.loadNext(second !== undefined && this.chapterOffset(second) <= top - EDGE_PX);
    }
    if (!reaching && top < EDGE_PX && (scrollable || this.store.atEnd())) {
      const last = loaded.at(-1);
      void this.store.loadPrevious(
        last !== undefined && this.chapterOffset(last) >= top + size + EDGE_PX,
      );
    }
  }

  /**
   * Scrolls on to {@link scrollTarget} when the content below it has grown; true while the target
   * is still out of reach. It is given up once reached, gone, or the book has nothing more to load.
   */
  private reachScrollTarget(list: MeasuredList<ReaderRow>, top: number, bottom: number): boolean {
    const chapterId = this.scrollTarget;
    if (!chapterId) return false;
    const target = this.chapterOffset(chapterId);
    if (!Number.isFinite(target) || top >= target - 1 || this.store.atEnd()) {
      this.scrollTarget = null;
      return false;
    }
    if (bottom >= 1) list.scrollToOffset(target);
    return true;
  }

  /** Where a loaded chapter's header row starts; a chapter with no rows sits off screen. */
  private chapterOffset(chapterId: string): number {
    const index = this.chapterRowIndex(chapterId);
    const list = this.list();
    return index < 0 || !list ? Number.POSITIVE_INFINITY : list.offsetOf(index);
  }

  /** The row index of a chapter's header, -1 when the chapter is not in the window. */
  private chapterRowIndex(chapterId: string): number {
    return this.rows().findIndex((r) => r.kind === 'chapter' && r.chapterId === chapterId);
  }
}

const rowKey = (row: ReaderRow) => row.key;

const EMPTY_CONTEXT: RowContext = {
  folder: '',
  mode: 'read',
  speakers: { names: {}, narrator: null },
  paragraphStatus: {},
  itemStatus: {},
  voices: {},
  reviews: {},
  selectable: false,
  selected: new Set(),
  itemSelectable: false,
  selectedItems: new Set(),
  narratorOnlyMode: false,
  ancestry: {},
  roster: [],
  blocked: null,
  viewing: false,
  generating: false,
  marks: {},
  litMarks: NO_LIT,
};

define('r2m-book-page', BookPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-book-page': BookPage;
  }
}
