import {
  AttributionApi,
  BookApi,
  type BookCommand,
  type Guid,
  type NarrationReleaseScope,
  type ParagraphItemDto,
  toApiError,
} from '@app/api';
import { use } from '@app/core/services';
import { ConfirmService, PromptService, confirmPermanent } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import type { ProjectStore } from '../project/project-store';
import type { BookEditor } from './book-editor';
import type { BookStore } from './book-store';
import { NOTHING_TO_ASSIGN, bulkConfirm, bulkDoneMessage } from './bulk-assign';
import { paragraphSweep, speakerChange } from './permanent-changes';
import {
  releaseConfirm,
  releaseDoneMessage,
  releaseNeedsConfirm,
  releaseNothingMessage,
} from './release-narration';

/** What a speaker menu pick lands on: one item, one paragraph, or the whole selection. */
export type AssignTarget =
  | { kind: 'item'; itemId: Guid; paragraphId: Guid }
  | { kind: 'paragraph'; paragraphId: Guid }
  | { kind: 'selection'; paragraphIds: Guid[] };

/**
 * Every speaker write the reader makes (ticket 12, research §3 "Speaker chip menu"): assign or
 * clear on an item or a paragraph, the bulk assign behind a preview and a confirm, and creating a
 * character to assign in the same gesture. Writes go through {@link BookEditor} (own receipt, then
 * the rows reload); a paragraph's remembered Failed/Unfinished outcome is forgotten with the
 * assign, as a stamp by hand settles what the queue could not. Page-scoped like the editor: the
 * project shell provides one per project, and the row partials reach it through DOM ancestry.
 */
export class SpeakerAssigner {
  private readonly api = use(BookApi);
  private readonly attribution = use(AttributionApi);
  private readonly confirm = use(ConfirmService);
  private readonly prompt = use(PromptService);
  private readonly toast = use(ToastService);

  constructor(
    private readonly editor: BookEditor,
    private readonly book: BookStore,
    private readonly project: ProjectStore,
  ) {}

  /**
   * `characterId` null clears the speaker (the line needs attributing again). A pick or clear that
   * discards audio asks first (permanent-change confirm, the delivery-cue precedent); one that
   * moves no audio goes straight through.
   */
  async assign(target: AssignTarget, characterId: Guid | null): Promise<void> {
    // Keyed on the id, not on the roster: an id the roster cannot explain still reads as an assign.
    const name =
      characterId === null ? null : (this.book.speakers().names[characterId] ?? 'the character');
    await this.write(target, characterId, name, (t) => speakerCommand(t, characterId));
  }

  /**
   * "New character…" from the menu: one `CreateCharacterAndAssign`, so the create and the assign
   * are one request (idempotent by name — an existing character or alias is assigned, not
   * doubled). A blank name (the menu's search was empty) asks for one first; a cancelled prompt
   * writes nothing. The audio confirm comes before the write, so it reckons with the roster's
   * names: a name nobody goes by yet is a new speaker on every line it lands on.
   */
  async createAndAssign(target: AssignTarget, name: string): Promise<void> {
    const named = (
      name.trim() ||
      (await this.prompt.text({
        title: 'New character',
        label: 'Name',
        required: true,
        confirmLabel: 'Create',
      })) ||
      ''
    ).trim();
    if (!named) return;
    await this.write(target, this.knownId(named) ?? NEW_CHARACTER, named, (t) => ({
      type: 'CreateCharacterAndAssign',
      name: named,
      ...targetFields(t),
    }));
  }

  /**
   * The shared gesture: an item or paragraph write asks first when it discards audio, then
   * forgets the paragraph's remembered outcome; a selection goes through the bulk preview.
   */
  private async write(
    target: AssignTarget,
    characterId: Guid | null,
    name: string | null,
    command: (target: AssignTarget) => BookCommand,
  ): Promise<void> {
    if (target.kind === 'selection') {
      return this.assignSelection(target.paragraphIds, name, command(target));
    }
    if (!(await confirmPermanent(speakerChange(this.stampedItems(target), characterId)))) return;
    if (await this.editor.run(command(target))) await this.clearOutcome(target.paragraphId);
  }

  /**
   * The roster character already answering to `name` (case-insensitive, name or alias, as the host
   * resolves it), if the loaded book knows one.
   */
  private knownId(name: string): Guid | null {
    const wanted = name.toLowerCase();
    const match = (this.book.overview()?.characters ?? []).find(
      (c) =>
        c.name.toLowerCase() === wanted || c.aliases.some((a) => a.name.toLowerCase() === wanted),
    );
    return match?.id ?? null;
  }

  /** A paragraph's Failed/Unfinished chip is cleared by hand (the queue state only). */
  async clearOutcome(paragraphId: Guid): Promise<void> {
    const folder = this.book.folder();
    if (!folder) return;
    try {
      await this.attribution.clearOutcome(folder, paragraphId);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    }
  }

  /**
   * The items the host will stamp for an item or paragraph target, from the loaded window (a row's
   * chip is always in it): the one item, or the paragraph's sweep. Empty when the row is gone.
   */
  private stampedItems(
    target: Exclude<AssignTarget, { kind: 'selection' }>,
  ): readonly ParagraphItemDto[] {
    const paragraph = this.book
      .chapters()
      .flatMap((c) => c.paragraphs)
      .find((p) => p.id === target.paragraphId);
    if (!paragraph) return [];
    if (target.kind === 'paragraph') return paragraphSweep(paragraph);
    return paragraph.items.filter((i) => i.id === target.itemId);
  }

  private async assignSelection(
    paragraphIds: Guid[],
    name: string | null,
    command: BookCommand,
  ): Promise<void> {
    const folder = this.book.folder();
    if (!folder || paragraphIds.length === 0) return;

    let preview;
    try {
      preview = await this.api.bulkAssignPreview(folder, paragraphIds);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return;
    }

    const options = bulkConfirm(name, preview, paragraphIds.length);
    if (!options) {
      this.toast.info(NOTHING_TO_ASSIGN);
      return;
    }
    if (!(await this.confirm.confirm(options))) return;

    if (!(await this.editor.run(command))) return;

    // Only the paragraphs the queue still remembers something about; usually none.
    const statuses = this.project.paragraphs();
    await Promise.all(
      paragraphIds.filter((id) => statuses[id]?.outcome).map((id) => this.clearOutcome(id)),
    );
    this.toast.success(bulkDoneMessage(name, preview));
  }

  /**
   * Release narration over a part, chapter or paragraph: preview, confirm, then one command. A
   * single paragraph without audio skips the confirm (`releaseNeedsConfirm`). Nothing to
   * release is a notice, not a confirm. Never enqueues attribution: the producer picks next
   * between bulk assign and "Attribute unprocessed". True when the release was written.
   */
  async releaseNarration(
    scope: NarrationReleaseScope,
    nodeId: Guid,
    nodeTitle: string,
  ): Promise<boolean> {
    const folder = this.book.folder();
    if (!folder) return false;

    let preview;
    try {
      preview = await this.api.releaseNarrationPreview(folder, scope, nodeId);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    }

    const options = releaseConfirm(scope, nodeTitle, preview);
    if (!options) {
      this.toast.info(releaseNothingMessage(scope));
      return false;
    }
    if (releaseNeedsConfirm(scope, preview) && !(await this.confirm.confirm(options))) return false;

    if (!(await this.editor.run({ type: 'ReleaseNarration', scope, nodeId }))) return false;
    this.toast.success(releaseDoneMessage(preview));
    return true;
  }
}

/** Stands in for a character the host has not created yet: no line is stamped with it. */
const NEW_CHARACTER = 'new-character';

/** The speaker write for a known id (null clears), one command per target kind. */
function speakerCommand(target: AssignTarget, characterId: Guid | null): BookCommand {
  switch (target.kind) {
    case 'item':
      return { type: 'SetItemCharacter', itemId: target.itemId, characterId };
    case 'paragraph':
      return { type: 'SetParagraphCharacter', paragraphId: target.paragraphId, characterId };
    case 'selection':
      return { type: 'SetParagraphsCharacter', paragraphIds: target.paragraphIds, characterId };
  }
}

/** The one target `CreateCharacterAndAssign` names, by its wire field. */
function targetFields(target: AssignTarget) {
  switch (target.kind) {
    case 'item':
      return { itemId: target.itemId };
    case 'paragraph':
      return { paragraphId: target.paragraphId };
    case 'selection':
      return { paragraphIds: target.paragraphIds };
  }
}
