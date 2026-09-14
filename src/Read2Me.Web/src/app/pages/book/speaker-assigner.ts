import { Injectable, inject } from '@angular/core';
import { AttributionApi, BookApi, Guid, toApiError } from '@app/api';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { ToastService } from '@app/ui/toast/toast.service';
import { ProjectStore } from '../project/project-store';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import { NOTHING_TO_ASSIGN, bulkConfirm, bulkDoneMessage } from './bulk-assign';

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
 * assign, as a stamp by hand settles what the queue could not.
 */
@Injectable()
export class SpeakerAssigner {
  private readonly editor = inject(BookEditor);
  private readonly book = inject(BookStore);
  private readonly project = inject(ProjectStore);
  private readonly api = inject(BookApi);
  private readonly attribution = inject(AttributionApi);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

  /** `characterId` null clears the speaker (the line needs attributing again). */
  async assign(target: AssignTarget, characterId: Guid | null): Promise<void> {
    switch (target.kind) {
      case 'item': {
        const ok = await this.editor.run({
          type: 'SetItemCharacter',
          itemId: target.itemId,
          characterId,
        });
        if (ok) await this.clearOutcome(target.paragraphId);
        return;
      }
      case 'paragraph': {
        const ok = await this.editor.run({
          type: 'SetParagraphCharacter',
          paragraphId: target.paragraphId,
          characterId,
        });
        if (ok) await this.clearOutcome(target.paragraphId);
        return;
      }
      case 'selection':
        return this.assignSelection(target.paragraphIds, characterId);
    }
  }

  /**
   * "New character…" from the menu: creates (idempotent by name — an existing character or alias
   * answers with its own id) and assigns in one go. Nothing happens on a blank name.
   */
  async createAndAssign(target: AssignTarget, name: string): Promise<void> {
    const id = await this.createCharacter(name);
    if (id) await this.assign(target, id);
  }

  /** The id of the character called `name`, created if nobody goes by it yet; null when refused. */
  async createCharacter(name: string): Promise<Guid | null> {
    const trimmed = name.trim();
    if (!trimmed) return null;
    const response = await this.editor.execute({ type: 'CreateCharacter', name: trimmed });
    if (!response) return null;
    if (response.newEntityId) return response.newEntityId;
    // The host has always answered the resolved id; this is the belt for the braces.
    return this.resolveByName(trimmed);
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

  private async assignSelection(paragraphIds: Guid[], characterId: Guid | null): Promise<void> {
    const folder = this.book.folder();
    if (!folder || paragraphIds.length === 0) return;

    let preview;
    try {
      preview = await this.api.bulkAssignPreview(folder, paragraphIds);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return;
    }

    // Keyed on the id, not on the roster: an id the roster cannot explain still reads as an assign.
    const name =
      characterId === null ? null : (this.book.speakers().names[characterId] ?? 'the character');
    const options = bulkConfirm(name, preview, paragraphIds.length);
    if (!options) {
      this.toast.info(NOTHING_TO_ASSIGN);
      return;
    }
    if (!(await this.confirm.confirm(options))) return;

    const ok = await this.editor.run({ type: 'SetParagraphsCharacter', paragraphIds, characterId });
    if (!ok) return;

    // Only the paragraphs the queue still remembers something about; usually none.
    const statuses = this.project.paragraphs();
    await Promise.all(
      paragraphIds.filter((id) => statuses[id]?.outcome).map((id) => this.clearOutcome(id)),
    );
    this.toast.success(bulkDoneMessage(name, preview));
  }

  private async resolveByName(name: string): Promise<Guid | null> {
    const folder = this.book.folder();
    if (!folder) return null;
    const wanted = name.toLowerCase();
    const characters = await this.api.characters(folder);
    const match = characters.find(
      (c) =>
        c.name.toLowerCase() === wanted || c.aliases.some((a) => a.name.toLowerCase() === wanted),
    );
    return match?.id ?? null;
  }
}
