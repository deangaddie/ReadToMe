import {
  type PronunciationDto,
  PronunciationsApi,
  type SetPronunciationRequest,
  toApiError,
} from '@app/api';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { deleteConfirm } from './permanent-changes';
import { savedToast } from './pronunciation-logic';

/**
 * Every Pronunciation write, shared by the editor and the list's delete buttons: the call, the
 * delete confirm, the toast ("Saved. 12 items need generating." with Show in Book only, no Undo
 * and no queue offer) and a refusal handed to `ToastService.problem()`. The receipt the write
 * lands refreshes the list, so nothing here touches page state. Each answers whether it wrote.
 */
export class PronunciationWriter {
  private readonly api = use(PronunciationsApi);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);

  /** Adds (`entry` null) or edits an entry. */
  async save(
    folder: string,
    entry: PronunciationDto | null,
    forms: SetPronunciationRequest,
  ): Promise<boolean> {
    try {
      const saved = entry
        ? await this.api.edit(folder, entry.id, forms)
        : await this.api.add(folder, forms);
      this.done(folder, 'Saved', saved.needsAudioCount);
      return true;
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
      return false;
    }
  }

  /** Deletes an entry once the confirm, which names the audio it costs, says so. */
  async remove(folder: string, entry: PronunciationDto): Promise<boolean> {
    try {
      const cost = await this.api.impact(folder, {
        id: entry.id,
        writtenForm: null,
        spokenForm: null,
        delete: true,
      });
      if (!(await this.confirm.confirm(deleteConfirm(entry.writtenForm, cost.audioDiscardCount)))) {
        return false;
      }
      const deleted = await this.api.remove(folder, entry.id);
      this.done(folder, 'Deleted', deleted.needsAudioCount);
      return true;
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
      return false;
    }
  }

  /** Show in Book opens the reader in Audio mode, where "select needs audio" is in reach. */
  private done(folder: string, verb: 'Saved' | 'Deleted', needsAudioCount: number): void {
    const message = savedToast(verb, needsAudioCount);
    if (needsAudioCount <= 0) {
      this.toast.success(message);
      return;
    }
    this.toast.info(message, [
      {
        label: 'Show in Book',
        run: () =>
          use(Router).navigate(`projects/${encodeURIComponent(folder)}/book`, {
            query: { mode: 'audio' },
          }),
      },
    ]);
  }
}
