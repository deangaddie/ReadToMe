import { computed, effect, untracked } from '@app/core/signals';
import type { ProjectStore } from '../project/project-store';
import type { AudioGenerator } from './audio-generator';
import type { BookStore } from './book-store';
import type { TreeNode } from './book-tree';
import type { SelectionKind } from './node-menu-entries';
import { type TriState, chapterDialogTotals, chapterVoicedTotals } from './selection';
import type { AudioSelectionStore, SelectionStore } from './selection-store';
import type { SpeakerAssigner } from './speaker-assigner';

/**
 * The Book page's selection workflow (spec #147), provided by the project shell beside the two
 * selections it works over: the {@link SelectionStore} of paragraphs in Read and Speakers modes,
 * the {@link AudioSelectionStore} of items in Audio. It derives which selection is active
 * ({@link kind}) and the tree's tri-state roll-up over it ({@link nodeStates}), and teaches both
 * stores their totals from every loaded chapter in its own effect, stopped by {@link dispose}.
 * The assigner and generator are for the intents (#150).
 */
export class SelectionWorkflow {
  /** Which selection the mode is in: items in Audio, paragraphs otherwise. */
  readonly kind = computed<SelectionKind>(() =>
    this.book.mode() === 'audio' ? 'items' : 'paragraphs',
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
  private readonly narratorOnlyMode = computed(
    () => this.store.detail()?.narratorOnlyMode ?? false,
  );
  private readonly stopLearning: () => void;

  constructor(
    private readonly book: BookStore,
    private readonly store: ProjectStore,
    private readonly selection: SelectionStore,
    private readonly audioSelection: AudioSelectionStore,
    // Read by the intents (#150).
    private readonly assigner: SpeakerAssigner,
    private readonly generator: AudioGenerator,
  ) {
    // A loaded chapter tells each selection how many rows of its kind it holds.
    this.stopLearning = effect(() => {
      const chapters = this.book.chapters();
      const dialog = chapterDialogTotals(chapters);
      const voiced = chapterVoicedTotals(chapters, this.narratorOnlyMode());
      untracked(() => {
        this.selection.learnTotals(dialog);
        this.audioSelection.learnTotals(voiced);
      });
    });
  }

  dispose(): void {
    this.stopLearning();
  }

  private active(): SelectionStore {
    return this.kind() === 'items' ? this.audioSelection : this.selection;
  }
}
