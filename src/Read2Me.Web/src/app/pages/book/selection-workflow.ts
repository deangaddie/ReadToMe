import {
  AttributionApi,
  BookApi,
  type Guid,
  type NarrationReleaseScope,
  type ParagraphDto,
  toApiError,
} from '@app/api';
import { use } from '@app/core/services';
import { computed, effect, signal, untracked } from '@app/core/signals';
import { enqueueWithPreflight } from '@app/shared/enqueue';
import { confirmPermanent } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import type { ProjectStore } from '../project/project-store';
import type { AudioGenerator } from './audio-generator';
import type { BookStore } from './book-store';
import type { TreeNode } from './book-tree';
import type { ActionEntryId, SelectionKind } from './node-menu-entries';
import { attributeSelectionChange, generateAudioChange } from './permanent-changes';
import { type TriState, chapterDialogTotals, chapterVoicedTotals } from './selection';
import type { AudioSelectionStore, SelectionStore } from './selection-store';
import type { SpeakerAssigner } from './speaker-assigner';

/** What a selection shortcut needs of the node it was chosen on: a tree node or a Book page heading. */
export type ActionNode = Pick<TreeNode, 'level' | 'id' | 'title'>;

/**
 * The Book page's selection workflow (spec #147), provided by the project shell beside the two
 * selections it works over: the {@link SelectionStore} of paragraphs in Read and Speakers modes,
 * the {@link AudioSelectionStore} of items in Audio. It derives which selection is active
 * ({@link kind}) and the tree's tri-state roll-up over it ({@link nodeStates}), and teaches both
 * stores their totals from every loaded chapter in its own effect, stopped by {@link dispose}.
 * Its intents are what the page forwards: tree checkboxes, node shortcuts, Release narration, and
 * the bars' Attribute and Generate audio. None throws; a failure is a problem toast.
 */
export class SelectionWorkflow {
  private readonly api = use(BookApi);
  private readonly attribution = use(AttributionApi);
  private readonly toast = use(ToastService);

  /** Which selection the mode is in: items in Audio, paragraphs otherwise. */
  readonly kind = computed<SelectionKind>(() =>
    this.book.mode() === 'audio' ? 'items' : 'paragraphs',
  );

  /** The selection {@link kind} names. */
  readonly active = computed<SelectionStore>(() =>
    this.kind() === 'items' ? this.audioSelection : this.selection,
  );

  /** Every tree node's checkbox state over the active selection. */
  readonly nodeStates = computed<Readonly<Record<string, TriState>>>(() => {
    const active = this.active();
    const states: Record<string, TriState> = {};
    const walk = (nodes: readonly TreeNode[]) => {
      for (const node of nodes) {
        states[node.id] = active.nodeState(node.level, node.id);
        walk(node.children);
      }
    };
    walk(this.book.tree());
    return states;
  });

  /** Narrator-only mode reads unattributed lines too, so they count as voiced. */
  readonly narratorOnlyMode = computed(() => this.store.detail()?.narratorOnlyMode ?? false);

  private readonly _working = signal(false);
  /** A selection read or attribution enqueue in flight: the paragraph bar waits for it. */
  readonly working = this._working.asReadonly();

  private readonly stopLearning: () => void;

  constructor(
    private readonly book: BookStore,
    private readonly store: ProjectStore,
    private readonly selection: SelectionStore,
    private readonly audioSelection: AudioSelectionStore,
    private readonly assigner: SpeakerAssigner,
    private readonly generator: AudioGenerator,
  ) {
    // A loaded chapter tells each selection how many rows of its kind it holds.
    this.stopLearning = effect(() => {
      this.book.chapters();
      this.narratorOnlyMode();
      untracked(() => this.relearn());
    });
  }

  dispose(): void {
    this.stopLearning();
  }

  /**
   * A tree checkbox: everything the mode's selection holds under the node — Character paragraphs,
   * or voiced items in Audio mode — joins or leaves it.
   */
  toggleNode(node: Pick<TreeNode, 'level' | 'id'>, on: boolean): Promise<void> {
    return this.withRead(async (folder) => {
      const store = this.active();
      const refs =
        this.kind() === 'items'
          ? await this.api.itemIds(folder, node.level, node.id, {
              narratorOnlyMode: this.narratorOnlyMode(),
            })
          : await this.api.paragraphIds(folder, node.level, node.id);
      // A full read is the node's total, whichever way the box went.
      store.learnTotals({ [node.id]: refs.length });
      if (on) store.add(refs);
      else store.remove(refs.map((r) => r.id));
    });
  }

  /**
   * A Volume, Part or Chapter's selection shortcuts, from the tree or a Book page heading, for
   * whichever selection the mode is in. The release names the node by the title it shows.
   */
  async nodeAction(node: ActionNode, action: ActionEntryId): Promise<void> {
    switch (action) {
      case 'select-unprocessed':
        // The node's unprocessed paragraphs, without loading every chapter.
        await this.withRead(async (folder) => {
          this.selection.add(await this.api.paragraphIds(folder, node.level, node.id, true));
        });
        return;
      case 'attribute-node': {
        // The node-scoped enqueue the overview also uses.
        const folder = this.book.folder();
        if (!folder) return;
        await this.enqueueAttribution(() =>
          this.attribution.enqueue(folder, {
            level: node.level,
            nodeId: node.id,
            unprocessedOnly: true,
          }),
        );
        return;
      }
      case 'select-needs-audio':
        // The node's Generatable items (voiced, still missing a WAV), without loading chapters.
        await this.withRead(async (folder) => {
          this.audioSelection.add(
            await this.api.itemIds(folder, node.level, node.id, {
              needsAudioOnly: true,
              narratorOnlyMode: this.narratorOnlyMode(),
            }),
          );
        });
        return;
      case 'generate-audio-node':
        await this.generator.enqueueNode(node.level, node.id, this.narratorOnlyMode());
        return;
      case 'release-narration':
        if (node.level === 'Volume') return;
        await this.release(node.level === 'Part' ? 'Part' : 'Chapter', node.id, node.title);
        return;
    }
  }

  /** A narration-only paragraph's Release narration, named by its text. */
  releaseParagraph(paragraphId: Guid, text: string): Promise<void> {
    return this.release('Paragraph', paragraphId, text);
  }

  /**
   * The paragraph bar's Attribute: asks first when a selected paragraph already has a speaker or
   * audio (permanent-change confirm), then queues the selection and clears it: the queue owns it
   * now.
   */
  async attribute(): Promise<void> {
    const ids = this.selection.ids();
    const folder = this.book.folder();
    if (ids.length === 0 || !folder) return;
    const paragraphs = await this.selectedParagraphs(this.selection);
    if (!paragraphs || !(await confirmPermanent(attributeSelectionChange(paragraphs)))) return;
    const queued = await this.enqueueAttribution(() =>
      this.attribution.enqueueParagraphs(folder, ids),
    );
    if (queued) this.selection.clear();
  }

  /**
   * The item bar's Generate audio: asks first when a selected item already has a take, then
   * queues the selection and clears it: the queue owns it now.
   */
  async generate(): Promise<void> {
    const ids = this.audioSelection.ids();
    if (ids.length === 0) return;
    const paragraphs = await this.selectedParagraphs(this.audioSelection);
    if (!paragraphs) return;
    const wanted = new Set(ids);
    const items = paragraphs.flatMap((p) => p.items.filter((i) => wanted.has(i.id)));
    if (!(await confirmPermanent(generateAudioChange(items)))) return;
    const queued = await this.generator.enqueueItems(ids);
    if (queued) this.audioSelection.clear();
  }

  /**
   * A release flips paragraphs into Character paragraphs, so both selections clear and their
   * learnt totals reseed (CONTEXT.md "Character paragraph"): the receipt reloads the rows, but a
   * part's total read before the release would undercount. Loaded chapters relearn theirs now, and
   * again when the receipt's reload lands.
   */
  private async release(scope: NarrationReleaseScope, nodeId: Guid, title: string): Promise<void> {
    if (!(await this.assigner.releaseNarration(scope, nodeId, title))) return;
    this.selection.reset();
    this.audioSelection.reset();
    // The chapters have not changed yet, so the learning effect would not run on its own.
    this.relearn();
  }

  /** Both selections' totals from the loaded chapters. */
  private relearn(): void {
    const chapters = this.book.chapters();
    this.selection.learnTotals(chapterDialogTotals(chapters));
    this.audioSelection.learnTotals(chapterVoicedTotals(chapters, this.narratorOnlyMode()));
  }

  private enqueueAttribution(request: () => Promise<{ enqueued: number }>): Promise<boolean> {
    return enqueueWithPreflight({
      busy: this._working,
      task: 'attribution',
      unit: 'paragraph',
      request,
    });
  }

  /**
   * The paragraphs a selection names (its own, or those holding its items), from the loaded
   * window where it has them and by a chapter read where it does not: a tree checkbox selects
   * across chapters the reader never opened. Null when a read failed (toasted).
   */
  private async selectedParagraphs(selection: SelectionStore): Promise<ParagraphDto[] | null> {
    const folder = this.book.folder();
    if (!folder) return null;
    const chapters = new Map(this.book.chapters().map((c) => [c.id, c.paragraphs]));
    const missing = new Set(
      Object.values(selection.selection())
        .map((a) => a.chapterId)
        .filter((id) => !chapters.has(id)),
    );
    if (missing.size > 0) {
      this._working.set(true);
      try {
        const reads = await Promise.all(
          [...missing].map(
            async (id) => [id, await this.api.children(folder, 'Chapter', id)] as const,
          ),
        );
        for (const [id, children] of reads) chapters.set(id, children.paragraphs ?? []);
      } catch (error) {
        this.toast.problem(toApiError(error).toProblem());
        return null;
      } finally {
        this._working.set(false);
      }
    }
    const ids = new Set(selection.ids());
    const paragraphs: ParagraphDto[] = [];
    for (const list of chapters.values()) {
      for (const p of list) {
        if (ids.has(p.id) || p.items.some((i) => ids.has(i.id))) paragraphs.push(p);
      }
    }
    return paragraphs;
  }

  /** A selection read behind {@link working}; a failure is a problem toast. */
  private async withRead(read: (folder: string) => Promise<void>): Promise<void> {
    const folder = this.book.folder();
    if (!folder) return;
    this._working.set(true);
    try {
      await read(folder);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this._working.set(false);
    }
  }
}
